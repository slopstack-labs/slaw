using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SlopstackLabs.Slaw.Setup;

/// <summary>
/// The setup window: a borderless studio-dark panel with the DJ deck on top
/// and one page of content below. Pages are rebuilt on every transition,
/// because state is a legacy concept.
/// </summary>
internal sealed class SetupWindow : Window
{
    private const string LicenseUrl = "https://github.com/slopstack-labs/slaw/blob/main/LICENSE";

    private static readonly string[] InstallQuips =
    {
        "Dropping the needle…",
        "Pressing vinyl…",
        "Negotiating tempo with the inference layer…",
        "Warming up the clipping substrate…",
        "Aligning stakeholders to the grid…",
        "Calibrating time-to-tinnitus…",
        "Synthesizing samples nobody recorded on purpose…",
    };

    private static readonly string[] UninstallQuips =
    {
        "Backspinning…",
        "Unpressing vinyl…",
        "Returning your speakers to their natural state…",
        "Offboarding the AI co-pilot. It has thoughts about this…",
    };

    private readonly string version;
    private readonly DjDeck deck = new();
    private readonly Grid page = new();
    private readonly StackPanel footerLeft = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel footerRight = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly DispatcherTimer quipTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private readonly TextBlock closeGlyph;

    private readonly OptionToggle desktopShortcut = new("Desktop shortcut", "Put slaw studio where stakeholders can see it.", true);
    private readonly OptionToggle addToPath = new("Add slaw to PATH", "Run compose, render and play from any terminal.", true);
    private readonly OptionToggle fileAssociation = new("Open .slaw projects with slaw", "Double-click a project to open it in the studio.", true);
    private readonly OptionToggle streamDeckPlugins = new("Stream Deck plugins", "For Settings → Controllers to install. Optional hardware, optional plugins.", true);
    private readonly OptionToggle launchAfterInstall = new("Launch slaw studio", "Your speakers have been notified.", true);
    private readonly TextBlock folderText;

    private Border? progressFill;
    private TextBlock? progressPercent;
    private TextBlock? quipText;
    private string[] quips = InstallQuips;
    private int quipIndex;
    private bool uninstalled;

    public SetupWindow(string version, string defaultInstallFolder, bool interactive)
    {
        this.version = version;
        InstallFolder = defaultInstallFolder;

        Title = "slaw setup";
        Width = 600;
        Height = 666;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = SetupTheme.Font;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        folderText = SetupTheme.Label(defaultInstallFolder, 12.5, SetupTheme.Text);
        folderText.TextTrimming = TextTrimming.CharacterEllipsis;
        folderText.TextWrapping = TextWrapping.NoWrap;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(196) });
        layout.RowDefinitions.Add(new RowDefinition());

        // Title bar
        var titleBar = new Grid { Background = Brushes.Transparent };
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = SetupTheme.AccentGradient, Margin = new Thickness(0, 1, 9, 0) });
        brand.Children.Add(SetupTheme.Label("slaw setup", 12.5, SetupTheme.TextMuted, FontWeights.SemiBold));
        titleBar.Children.Add(brand);
        closeGlyph = SetupTheme.Label("✕", 13, SetupTheme.TextMuted);
        var closeButton = new Border
        {
            Width = 44,
            Height = 44,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = closeGlyph,
        };
        closeGlyph.HorizontalAlignment = HorizontalAlignment.Center;
        closeGlyph.VerticalAlignment = VerticalAlignment.Center;
        closeButton.MouseEnter += (_, _) => closeGlyph.Foreground = SetupTheme.Text;
        closeButton.MouseLeave += (_, _) => closeGlyph.Foreground = SetupTheme.TextMuted;
        closeButton.MouseLeftButtonUp += (_, _) => CancelRequested?.Invoke();
        titleBar.Children.Add(closeButton);
        layout.Children.Add(titleBar);

        // Deck
        var stage = new Border
        {
            Background = new LinearGradientBrush(SetupTheme.Color("#13131b"), SetupTheme.Color("#0e0e15"), 90),
            BorderBrush = SetupTheme.Border,
            BorderThickness = new Thickness(0, 1, 0, 1),
            Child = deck,
        };
        Grid.SetRow(stage, 1);
        layout.Children.Add(stage);

        // Page and footer
        var content = new Grid { Margin = new Thickness(32, 24, 32, 24) };
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(page);
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.Children.Add(footerLeft);
        footer.Children.Add(footerRight);
        Grid.SetRow(footer, 1);
        content.Children.Add(footer);
        Grid.SetRow(content, 2);
        layout.Children.Add(content);

        Content = new Border
        {
            Background = SetupTheme.Background,
            BorderBrush = SetupTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = new Border { CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = layout },
        };

        quipTimer.Tick += (_, _) => NextQuip();
        ShowPage(Hero("Getting ready", "Detecting what is already on this machine…"));

        if (!interactive)
        {
            closeButton.Visibility = Visibility.Collapsed;
        }
    }

    public event Action? InstallRequested;
    public event Action? RepairRequested;
    public event Action? UninstallRequested;
    public event Action? CancelRequested;
    public event Action? FinishRequested;
    public event Action? LaunchRequested;
    public event Action? OpenLogRequested;

    public string InstallFolder { get; private set; }

    public bool IsApplying { get; private set; }

    public Dictionary<string, bool> FeatureSelection() => new()
    {
        ["DesktopShortcutFeature"] = desktopShortcut.IsChecked,
        ["PathFeature"] = addToPath.IsChecked,
        ["FileAssociationFeature"] = fileAssociation.IsChecked,
        ["StreamDeckPluginsFeature"] = streamDeckPlugins.IsChecked,
    };

    public void ShowWelcome()
    {
        var body = Hero("slaw", "The AI-native digital audio workstation. Beyond deterministic production.", VersionChip());
        body.Children.Add(Spacer(18));
        body.Children.Add(desktopShortcut);
        body.Children.Add(addToPath);
        body.Children.Add(fileAssociation);
        body.Children.Add(streamDeckPlugins);
        body.Children.Add(Spacer(6));
        body.Children.Add(FolderRow());

        var licence = new StackPanel { Orientation = Orientation.Horizontal };
        licence.Children.Add(SetupTheme.Label("Installing accepts the ", 12.5, SetupTheme.TextMuted));
        licence.Children.Add(SetupTheme.Link("MIT license", () => Open(LicenseUrl)));
        var install = new PillButton("Install", primary: true);
        install.Click += () => InstallRequested?.Invoke();
        ShowPage(body, licence, install);
    }

    public void ShowMaintenance(bool uninstallRequested)
    {
        var body = uninstallRequested
            ? Hero("Uninstall slaw?", "This removes slaw, its shortcuts, its PATH entry and the .slaw file association. Your projects stay where they are.", VersionChip())
            : Hero("slaw is already installed", "Repair puts back anything that went missing. Uninstall removes slaw, its shortcuts, its PATH entry and the .slaw file association.", VersionChip());

        var uninstall = new PillButton("Uninstall", primary: uninstallRequested);
        uninstall.Click += () => UninstallRequested?.Invoke();
        if (uninstallRequested)
        {
            var keep = new PillButton("Keep it", primary: false);
            keep.Click += () => CancelRequested?.Invoke();
            ShowPage(body, null, keep, uninstall);
            return;
        }

        var repair = new PillButton("Repair", primary: true);
        repair.Click += () => RepairRequested?.Invoke();
        ShowPage(body, null, uninstall, repair);
    }

    public void ShowProgress(bool uninstalling)
    {
        IsApplying = true;
        uninstalled = uninstalling;
        quips = uninstalling ? UninstallQuips : InstallQuips;
        quipIndex = 0;

        var body = Hero(uninstalling ? "Uninstalling slaw" : "Installing slaw", "");
        quipText = (TextBlock)body.Children[1];
        quipText.Text = quips[0];
        body.Children.Add(Spacer(26));

        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        var track = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = SetupTheme.SurfaceRaised, VerticalAlignment = VerticalAlignment.Center };
        progressFill = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = SetupTheme.AccentGradient,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = new ScaleTransform(0, 1),
        };
        track.Child = progressFill;
        bar.Children.Add(track);
        progressPercent = SetupTheme.Label("0%", 13, SetupTheme.Text, FontWeights.SemiBold);
        progressPercent.HorizontalAlignment = HorizontalAlignment.Right;
        progressPercent.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(progressPercent, 1);
        bar.Children.Add(progressPercent);
        body.Children.Add(bar);

        var cancel = new PillButton("Cancel", primary: false);
        cancel.Click += () => CancelRequested?.Invoke();
        ShowPage(body, null, cancel);

        deck.SetProgress(0);
        deck.Play(reverse: uninstalling);
        quipTimer.Start();
    }

    public void SetProgress(int percent)
    {
        if (progressFill?.RenderTransform is not ScaleTransform scale || progressPercent is null)
        {
            return;
        }

        var fraction = Math.Max(0, Math.Min(100, percent)) / 100.0;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(fraction, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        progressPercent.Text = $"{percent}%";
        deck.SetProgress(uninstalled ? 1 - fraction : fraction);
    }

    public void ShowCancelling()
    {
        quipTimer.Stop();
        if (quipText is not null)
        {
            quipText.Text = "Cancelling. Rolling back to the last known silence…";
        }

        foreach (UIElement button in footerRight.Children)
        {
            button.IsEnabled = false;
        }
    }

    public void ShowComplete(bool uninstalled, bool restartRequired)
    {
        IsApplying = false;
        quipTimer.Stop();
        deck.SetProgress(uninstalled ? 0 : 1);
        deck.Stop();

        var restartNote = restartRequired ? " Restart Windows to finish." : "";
        var finish = new PillButton("Finish", primary: true);
        if (uninstalled)
        {
            finish.Click += () => FinishRequested?.Invoke();
            ShowPage(Hero("slaw has left the building", "Uninstalled. Silence restored." + restartNote), null, finish);
            return;
        }

        var installedNote = string.IsNullOrEmpty(version) ? "slaw is installed." : $"slaw {version} is installed.";
        var body = Hero("Mastered. Sort of.", installedNote + restartNote);
        body.Children.Add(Spacer(18));
        body.Children.Add(launchAfterInstall);
        finish.Click += () =>
        {
            if (launchAfterInstall.IsChecked)
            {
                LaunchRequested?.Invoke();
            }

            FinishRequested?.Invoke();
        };
        ShowPage(body, null, finish);
    }

    public void ShowFailure(int status, bool cancelled)
    {
        IsApplying = false;
        quipTimer.Stop();
        deck.Stop();

        var body = cancelled
            ? Hero("Setup cancelled", "Nothing was shipped, for once.")
            : Hero("That take didn't land", $"Setup failed with error 0x{status:X8}. The log has the details, and possibly opinions.");
        ((TextBlock)body.Children[0]).Foreground = cancelled ? SetupTheme.Text : SetupTheme.Failure;

        var log = SetupTheme.Link("Open log", () => OpenLogRequested?.Invoke());
        var close = new PillButton("Close", primary: true);
        close.Click += () => FinishRequested?.Invoke();
        ShowPage(body, cancelled ? null : log, close);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4 mid-install means cancel, not abandon
        if (IsApplying)
        {
            e.Cancel = true;
            CancelRequested?.Invoke();
        }

        base.OnClosing(e);
    }

    private void ShowPage(StackPanel body, UIElement? left = null, params UIElement[] right)
    {
        page.Children.Clear();
        page.Children.Add(body);
        footerLeft.Children.Clear();
        if (left is not null)
        {
            footerLeft.Children.Add(left);
        }

        footerRight.Children.Clear();
        foreach (var button in right)
        {
            footerRight.Children.Add(button);
        }

        body.RenderTransform = new TranslateTransform(0, 8);
        var duration = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        body.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        body.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = ease });
    }

    private static StackPanel Hero(string title, string subtitle, UIElement? badge = null)
    {
        var body = new StackPanel();
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        var titleText = SetupTheme.Label(title, 26, SetupTheme.Text, FontWeights.SemiBold);
        titleText.FontFamily = SetupTheme.DisplayFont;
        titleText.TextWrapping = TextWrapping.NoWrap;
        body.Children.Add(titleText);
        var subtitleText = SetupTheme.Label(subtitle, 13.5, SetupTheme.TextMuted);
        subtitleText.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(subtitleText);

        if (badge is not null)
        {
            body.Children.Remove(titleText);
            heading.Children.Add(titleText);
            heading.Children.Add(badge);
            body.Children.Insert(0, heading);
        }

        return body;
    }

    private UIElement VersionChip()
    {
        if (string.IsNullOrEmpty(version))
        {
            return new Border();
        }

        return new Border
        {
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 3),
            CornerRadius = new CornerRadius(6),
            Background = SetupTheme.Solid("#5b1e66"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = SetupTheme.Label(version, 11.5, SetupTheme.Text, FontWeights.SemiBold),
        };
    }

    private UIElement FolderRow()
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = SetupTheme.Label("Install to", 12.5, SetupTheme.TextMuted);
        label.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(label);
        Grid.SetColumn(folderText, 1);
        row.Children.Add(folderText);
        var change = SetupTheme.Link("Change…", ChooseFolder);
        change.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(change, 2);
        row.Children.Add(change);
        return row;
    }

    private void ChooseFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Where should slaw live?",
            SelectedPath = InstallFolder,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            InstallFolder = dialog.SelectedPath;
            folderText.Text = InstallFolder;
        }
    }

    private void NextQuip()
    {
        if (quipText is null)
        {
            return;
        }

        quipIndex = (quipIndex + 1) % quips.Length;
        quipText.Text = quips[quipIndex];
        quipText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(300)));
    }

    private static Border Spacer(double height) => new() { Height = height };

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
