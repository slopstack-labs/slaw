using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>Something the palette can run.</summary>
internal sealed record CommandPaletteEntry(string Title, string? Gesture, Action Run);

/// <summary>Fuzzy matching in the manner of a code editor: type a few letters in order, in any spacing.</summary>
internal static class CommandPaletteMatcher
{
    /// <summary>A score for how well a query fits a title (higher is better), or null when it does not.</summary>
    public static int? Score(string query, string title)
    {
        var compactQuery = query.Replace(" ", string.Empty);
        if (compactQuery.Length == 0)
        {
            return 0;
        }
        var titleIndex = 0;
        var score = 0;
        var previousMatch = -2;
        foreach (var queryCharacter in compactQuery)
        {
            while (titleIndex < title.Length && char.ToLowerInvariant(title[titleIndex]) != char.ToLowerInvariant(queryCharacter))
            {
                titleIndex++;
            }
            if (titleIndex == title.Length)
            {
                return null;
            }
            score += 1;
            if (titleIndex == previousMatch + 1)
            {
                score += 4;
            }
            if (titleIndex == 0 || !char.IsLetterOrDigit(title[titleIndex - 1]))
            {
                score += 8;
            }
            previousMatch = titleIndex;
            titleIndex++;
        }
        if (title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }
        // Between equals, the shorter title is the more specific answer.
        return score * 100 - title.Length;
    }

    /// <summary>The titles matching a query, best first; ties keep their original order.</summary>
    public static IReadOnlyList<CommandPaletteEntry> Rank(string query, IEnumerable<CommandPaletteEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return entries.ToList();
        }
        return entries
            .Select(entry => (Entry: entry, Score: Score(query, entry.Title)))
            .Where(candidate => candidate.Score is not null)
            .OrderByDescending(candidate => candidate.Score)
            .Select(candidate => candidate.Entry)
            .ToList();
    }
}

/// <summary>
/// The command palette: a search box over everything the studio can do, floating over
/// the window it was opened in. Nothing behind it stops, so the song keeps playing
/// while you look for the thing you forgot the shortcut to.
/// </summary>
internal sealed class CommandPalette : Panel
{
    private const int MaximumRememberedRuns = 8;

    private readonly TextBox searchInput = new()
    {
        Watermark = "Type a command…",
        FontSize = 15,
        Padding = new Thickness(12, 9),
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
    };

    private readonly ListBox resultList = new()
    {
        MaxHeight = 360,
        Background = Brushes.Transparent,
    };

    private readonly TextBlock emptyCaption = new()
    {
        Text = "No matching commands",
        Foreground = StudioTheme.TextMuted,
        FontSize = 12,
        Margin = new Thickness(14, 10),
        IsVisible = false,
    };

    private readonly List<string> recentTitles = new();
    private IReadOnlyList<CommandPaletteEntry> allEntries = Array.Empty<CommandPaletteEntry>();

    public CommandPalette()
    {
        IsVisible = false;
        resultList.ItemTemplate = new FuncDataTemplate<CommandPaletteEntry>((entry, _) => ComposeRow(entry), supportsRecycling: false);

        var card = new Border
        {
            Width = 580,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(16, 56, 16, 16),
            Background = StudioTheme.SurfaceRaised,
            BorderBrush = StudioTheme.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 28, OffsetY = 8, Color = Color.FromArgb(170, 0, 0, 0) }),
            Child = new StackPanel
            {
                Children =
                {
                    searchInput,
                    new Border { Height = 1, Background = StudioTheme.Border },
                    resultList,
                    emptyCaption,
                },
            },
        };
        // Clicks on the card stay on the card; anything outside it dismisses the palette.
        card.PointerPressed += (_, pointerEvent) => pointerEvent.Handled = true;

        Children.Add(new Border { Background = new SolidColorBrush(Colors.Black, 0.35) });
        Children[0].PointerPressed += (_, _) => Close();
        Children.Add(card);

        searchInput.TextChanged += (_, _) => RefreshResults();
        searchInput.AddHandler(KeyDownEvent, HandleSearchKey, RoutingStrategies.Tunnel);
        resultList.Tapped += (_, _) => RunSelected();
    }

    public bool IsOpen => IsVisible;

    public void Open(IReadOnlyList<CommandPaletteEntry> entries)
    {
        allEntries = entries;
        searchInput.Text = string.Empty;
        IsVisible = true;
        RefreshResults();
        // After layout: a control cannot take focus while it is still invisible.
        Dispatcher.UIThread.Post(() => searchInput.Focus(), DispatcherPriority.Input);
    }

    public void Close()
    {
        if (!IsVisible)
        {
            return;
        }
        IsVisible = false;
        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
    }

    private void RefreshResults()
    {
        var query = searchInput.Text ?? string.Empty;
        var ranked = CommandPaletteMatcher.Rank(query, allEntries);
        if (string.IsNullOrWhiteSpace(query) && recentTitles.Count > 0)
        {
            // Empty box: what you ran last, then everything else.
            ranked = ranked.OrderBy(entry => recentTitles.IndexOf(entry.Title) is var recency and >= 0 ? recency : int.MaxValue).ToList();
        }
        resultList.ItemsSource = ranked;
        resultList.SelectedIndex = ranked.Count > 0 ? 0 : -1;
        resultList.IsVisible = ranked.Count > 0;
        emptyCaption.IsVisible = ranked.Count == 0;
    }

    private void HandleSearchKey(object? sender, KeyEventArgs keyEvent)
    {
        switch (keyEvent.Key)
        {
            case Key.Down:
            case Key.Tab when keyEvent.KeyModifiers == KeyModifiers.None:
                MoveSelection(1);
                break;
            case Key.Up:
            case Key.Tab:
                MoveSelection(-1);
                break;
            case Key.Enter:
                RunSelected();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        keyEvent.Handled = true;
    }

    private void MoveSelection(int direction)
    {
        var itemCount = resultList.ItemCount;
        if (itemCount == 0)
        {
            return;
        }
        resultList.SelectedIndex = (resultList.SelectedIndex + direction + itemCount) % itemCount;
        if (resultList.SelectedItem is { } selectedEntry)
        {
            resultList.ScrollIntoView(selectedEntry);
        }
    }

    private void RunSelected()
    {
        if (resultList.SelectedItem is not CommandPaletteEntry selectedEntry)
        {
            return;
        }
        recentTitles.Remove(selectedEntry.Title);
        recentTitles.Insert(0, selectedEntry.Title);
        if (recentTitles.Count > MaximumRememberedRuns)
        {
            recentTitles.RemoveAt(recentTitles.Count - 1);
        }
        Close();
        selectedEntry.Run();
    }

    private static Control ComposeRow(CommandPaletteEntry? entry)
    {
        if (entry is null)
        {
            return new Panel();
        }
        var row = new DockPanel { Margin = new Thickness(2, 1) };
        if (entry.Gesture is { } gesture)
        {
            var gestureCaption = new TextBlock
            {
                Text = gesture,
                FontFamily = StudioTheme.MonospaceFont,
                FontSize = 11,
                Foreground = StudioTheme.TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            DockPanel.SetDock(gestureCaption, Dock.Right);
            row.Children.Add(gestureCaption);
        }
        row.Children.Add(new TextBlock { Text = entry.Title, FontSize = 13, Foreground = StudioTheme.Text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
