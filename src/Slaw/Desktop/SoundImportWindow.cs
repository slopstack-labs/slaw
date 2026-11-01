using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>What the Import window hands back to the studio.</summary>
internal sealed record SoundImportRequest(SoundImportMode Mode, IReadOnlyList<string> SoundPaths, string Name, IReadOnlyList<string> Folders, IReadOnlyList<ImportedRack> Racks);

/// <summary>
/// The Import window: drop a folder or some files, hear what is in there, tick what
/// you want and say how it should arrive. Not modal, so the song can keep playing
/// while you decide whether this kick is the kick.
/// </summary>
internal sealed class SoundImportWindow : Window
{
    private const int MaximumScannedFiles = 2000;
    private const double DrumKitLongestAverageSeconds = 4;

    private static readonly FilePickerFileType AudioFileType = new("Audio files") { Patterns = new[] { "*.wav", "*.wave", "*.aif", "*.aiff", "*.aifc", "*.flac", "*.adg", "*.adv" } };
    private static readonly string[] AbletonOnlyExtensions = { ".alp", ".als" };

    private readonly Action<string> previewSound;
    private readonly Action<SoundImportRequest> import;
    private readonly double projectBpm;
    private readonly List<SoundRow> rows = new();
    private readonly List<string> sourceFolders = new();

    private readonly Border dropZone = new();
    private readonly DockPanel loadedPanel = new();
    private readonly TextBlock sourceCaption = new() { FontSize = 12, Foreground = StudioTheme.TextMuted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock noticeCaption = new() { FontSize = 12, Foreground = StudioTheme.Warning, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox filterInput = new() { Watermark = "filter…", FontSize = 12, MinHeight = 28, Width = 180 };
    private readonly ListBox soundList = new() { Background = Brushes.Transparent };
    private readonly TextBox nameInput = new() { FontSize = 13 };
    private readonly TextBlock nameCaption = StudioUi.Caption("Name", 11, StudioTheme.TextMuted);
    private readonly TextBlock summaryCaption = new() { FontSize = 12, Foreground = StudioTheme.TextMuted, TextWrapping = TextWrapping.Wrap };
    private readonly Button importButton;
    private readonly Dictionary<SoundImportMode, Border> modeCards = new();

    private SoundImportMode chosenMode = SoundImportMode.DrumKit;
    private bool modeChosenByHand;
    private bool scanning;
    private CancellationTokenSource? scanCancellation;

    public SoundImportWindow(Action<string> previewSound, Action<SoundImportRequest> import, double projectBpm)
    {
        this.projectBpm = projectBpm;
        this.previewSound = previewSound;
        this.import = import;
        Title = "slaw — Import sounds";
        Width = 900;
        Height = 620;
        MinWidth = 720;
        MinHeight = 460;
        Background = StudioTheme.Background;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        importButton = StudioUi.AccentButton("Import", ConfirmImport);
        soundList.Styles.Add(new Style(selector => selector.Is<ListBoxItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(8, 2)) },
        });
        soundList.ItemTemplate = new FuncDataTemplate<SoundRow>((row, _) => ComposeSoundRow(row), supportsRecycling: false);
        soundList.SelectionChanged += (_, _) =>
        {
            // The way Live's browser does it: landing on a sound plays it, so arrow keys audition a whole folder.
            if (soundList.SelectedItem is SoundRow { Failure: null } selectedRow)
            {
                previewSound(selectedRow.PreviewPath);
            }
        };
        soundList.KeyDown += (_, keyEvent) =>
        {
            if (keyEvent.Key == Key.Space && soundList.SelectedItem is SoundRow toggledRow && toggledRow.Failure is null)
            {
                toggledRow.Included = !toggledRow.Included;
                RefreshList(keepSelection: true);
                keyEvent.Handled = true;
            }
        };
        filterInput.TextChanged += (_, _) => RefreshList(keepSelection: false);
        nameInput.TextChanged += (_, _) => RefreshSummary();

        Content = ComposeLayout();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, dragEvent) => dragEvent.DragEffects = dragEvent.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, dragEvent) =>
        {
            var droppedPaths = dragEvent.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList();
            if (droppedPaths is { Count: > 0 })
            {
                _ = LoadSourcesAsync(droppedPaths);
            }
        });
        KeyDown += (_, keyEvent) =>
        {
            if (keyEvent.Key == Key.Escape)
            {
                Close();
                keyEvent.Handled = true;
            }
            else if (keyEvent.Key == Key.Enter && importButton.IsEnabled && FocusManager?.GetFocusedElement() is not TextBox)
            {
                ConfirmImport();
                keyEvent.Handled = true;
            }
        };
        Closed += (_, _) => scanCancellation?.Cancel();
        RefreshSources();
        RefreshSummary();
    }

    /// <summary>Anything from Live: presets, which can be read, and songs and packs, which get an explanation.</summary>
    public static bool IsAbletonFile(string path) =>
        File.Exists(path) && (AbletonRackImporter.IsPresetFile(path) || AbletonOnlyExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));

    public static bool FolderHoldsPresets(string folder) =>
        Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 })
            .Any(AbletonRackImporter.IsPresetFile);

    /// <summary>Starts on one way of importing, as the palette's entries ask. It stays that way unless the user picks another.</summary>
    public void PresetMode(SoundImportMode mode)
    {
        modeChosenByHand = true;
        SelectMode(mode);
    }

    /// <summary>Starts from these paths, as if they had just been dropped.</summary>
    public Task LoadSourcesAsync(IReadOnlyList<string> droppedPaths)
    {
        scanCancellation?.Cancel();
        scanCancellation = new CancellationTokenSource();
        return ScanAsync(droppedPaths, scanCancellation.Token);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private Control ComposeLayout()
    {
        var chooseFolderButton = StudioUi.AccentButton("Choose folder…", () => _ = ChooseFolderAsync());
        var chooseFilesButton = StudioUi.Button("Choose files…", () => _ = ChooseFilesAsync());
        var buttonRow = StudioUi.Row(8, chooseFolderButton, chooseFilesButton);
        buttonRow.HorizontalAlignment = HorizontalAlignment.Center;
        dropZone.Background = StudioTheme.Surface;
        dropZone.BorderBrush = StudioTheme.Border;
        dropZone.BorderThickness = new Thickness(1);
        dropZone.CornerRadius = new CornerRadius(10);
        dropZone.Child = new StackPanel
        {
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Drop a folder or audio files here", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = StudioTheme.Text, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "WAV, AIFF, FLAC and Ableton presets (.adg). Folders are searched all the way down, so a sample pack works as it is.", FontSize = 12, Foreground = StudioTheme.TextMuted, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                buttonRow,
            },
        };

        var listTools = StudioUi.Row(6,
            filterInput,
            StudioUi.Button("All", () => SetVisibleIncluded(true), "Tick everything the filter shows", compact: true),
            StudioUi.Button("None", () => SetVisibleIncluded(false), "Untick everything the filter shows", compact: true),
            StudioUi.Button("Change…", () => _ = ChooseFolderAsync(), "Look somewhere else", compact: true));
        DockPanel.SetDock(listTools, Dock.Right);
        var listHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Children = { listTools, sourceCaption } };
        DockPanel.SetDock(listHeader, Dock.Top);
        loadedPanel.Children.Add(listHeader);
        var listFrame = new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = soundList,
        };
        loadedPanel.Children.Add(listFrame);

        var sourceArea = new Panel { Children = { dropZone, loadedPanel } };
        var leftColumn = new DockPanel();
        DockPanel.SetDock(noticeCaption, Dock.Bottom);
        leftColumn.Children.Add(noticeCaption);
        leftColumn.Children.Add(sourceArea);

        var rightColumn = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                StudioUi.Caption("Bring them in as", 13, StudioTheme.Text, FontWeight.SemiBold),
                ComposeModeCard(SoundImportMode.AbletonPreset, "Ableton preset", "One playable instrument per preset, with its key and velocity map, and its effects where slaw has them."),
                ComposeModeCard(SoundImportMode.DrumKit, "Drum kit", "One track, one sound per key, starting on C4. Best for one-shots."),
                ComposeModeCard(SoundImportMode.SeparateTracks, "Separate tracks", "A track for each sound, pitched across the keyboard."),
                ComposeModeCard(SoundImportMode.SampleLibrary, "Sample library", "Keep the folder in Browser → Samples, and add nothing to this song yet."),
                nameCaption,
                nameInput,
                summaryCaption,
            },
        };

        var workArea = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,270") };
        Grid.SetColumn(rightColumn, 2);
        workArea.Children.Add(leftColumn);
        workArea.Children.Add(rightColumn);

        var footerButtons = StudioUi.Row(8, StudioUi.Button("Cancel", Close), importButton);
        footerButtons.HorizontalAlignment = HorizontalAlignment.Right;
        footerButtons.Margin = new Thickness(0, 14, 0, 0);
        DockPanel.SetDock(footerButtons, Dock.Bottom);

        var heading = new TextBlock { Text = "Import sounds", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = StudioTheme.Text, Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(heading, Dock.Top);
        return new DockPanel { Margin = new Thickness(20), Children = { heading, footerButtons, workArea } };
    }

    private Border ComposeModeCard(SoundImportMode mode, string title, string description)
    {
        var card = new Border
        {
            Background = StudioTheme.Surface,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = StudioTheme.Text },
                    new TextBlock { Text = description, FontSize = 11, Foreground = StudioTheme.TextMuted, TextWrapping = TextWrapping.Wrap },
                },
            },
        };
        card.PointerPressed += (_, _) =>
        {
            modeChosenByHand = true;
            SelectMode(mode);
        };
        modeCards[mode] = card;
        return card;
    }

    private Control ComposeSoundRow(SoundRow? row)
    {
        if (row is null)
        {
            return new Panel();
        }
        var includedBox = new CheckBox { IsChecked = row.Included, IsEnabled = row.Failure is null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        includedBox.IsCheckedChanged += (_, _) =>
        {
            row.Included = includedBox.IsChecked == true;
            RefreshKitKeys();
            RefreshSummary();
        };
        var playButton = StudioUi.Button("▶", () => previewSound(row.PreviewPath), "Listen", compact: true);
        playButton.IsEnabled = row.Failure is null;

        var kindChip = new Border
        {
            Background = StudioTheme.SurfaceRaised,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = row.Kind.Label, FontSize = 10, Foreground = StudioTheme.TextMuted },
        };
        var detail = row.Failure is not null
            ? "can't read"
            : row.Rack is { Zones.Count: 0 } ? "synth"
            : row.Rack is { } rack ? rack.MissingFiles.Count > 0 ? $"{rack.Zones.Count} sounds, {rack.MissingFiles.Count} missing" : $"{rack.Zones.Count} sounds"
            : row.Seconds is { } seconds ? string.Create(CultureInfo.InvariantCulture, $"{seconds:0.00} s") : "…";
        var detailForeground = row.Failure is not null ? StudioTheme.Failure : row.Rack is { MissingFiles.Count: > 0 } ? StudioTheme.Warning : StudioTheme.TextMuted;
        var detailCaption = new TextBlock { Text = detail, FontSize = 11, Foreground = detailForeground, VerticalAlignment = VerticalAlignment.Center, MinWidth = 62, TextAlignment = TextAlignment.Right };
        var keyCaption = new TextBlock { Text = row.KitKey ?? string.Empty, FontFamily = StudioTheme.MonospaceFont, FontSize = 11, Foreground = StudioTheme.Accent, VerticalAlignment = VerticalAlignment.Center, Width = 34, TextAlignment = TextAlignment.Right };

        Control rightSlot = keyCaption;
        if (row.Rack is { Zones.Count: > 0 } mappedRack)
        {
            rightSlot = new SampleZoneMapControl { Width = 110, Height = 24, Zones = mappedRack.Zones, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(rightSlot, "Keys left to right, velocity bottom to top. Brighter blocks are layers.");
        }
        var rightGroup = StudioUi.Row(8, kindChip, detailCaption, rightSlot);
        DockPanel.SetDock(rightGroup, Dock.Right);
        var leftGroup = StudioUi.Row(8, includedBox, playButton);
        DockPanel.SetDock(leftGroup, Dock.Left);
        var rowName = new TextBlock { Text = row.Name, FontSize = 12, Foreground = row.Failure is null ? StudioTheme.Text : StudioTheme.TextMuted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0) };
        ToolTip.SetTip(rowName, row.Failure ?? row.Path);
        return new DockPanel { Children = { rightGroup, leftGroup, rowName } };
    }

    // ── Choosing sources ──────────────────────────────────────────────────────

    private async Task ChooseFolderAsync()
    {
        var chosenFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder of sounds", AllowMultiple = true });
        var chosenPaths = chosenFolders.Select(folder => folder.TryGetLocalPath()).OfType<string>().ToList();
        if (chosenPaths.Count > 0)
        {
            await LoadSourcesAsync(chosenPaths);
        }
    }

    private async Task ChooseFilesAsync()
    {
        var chosenFiles = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose sounds", AllowMultiple = true, FileTypeFilter = new[] { AudioFileType } });
        var chosenPaths = chosenFiles.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
        if (chosenPaths.Count > 0)
        {
            await LoadSourcesAsync(chosenPaths);
        }
    }

    private async Task ScanAsync(IReadOnlyList<string> sourcePaths, CancellationToken cancellation)
    {
        scanning = true;
        rows.Clear();
        sourceFolders.Clear();
        sourceFolders.AddRange(sourcePaths.Where(Directory.Exists).Select(Path.GetFullPath));
        var liveSong = sourcePaths.FirstOrDefault(path => string.Equals(Path.GetExtension(path), ".als", StringComparison.OrdinalIgnoreCase));
        var livePack = sourcePaths.FirstOrDefault(path => string.Equals(Path.GetExtension(path), ".alp", StringComparison.OrdinalIgnoreCase));
        if (liveSong is not null && Path.GetDirectoryName(Path.GetFullPath(liveSong)) is { } songFolder && !sourceFolders.Contains(songFolder))
        {
            // The song can't come, but the instruments and samples beside it can.
            sourceFolders.Add(songFolder);
        }
        ShowNotice(livePack is not null
            ? $"{Path.GetFileName(livePack)} is a packed Live Pack, which slaw can't unpack. Double-click it to install it in Live, then drop the installed folder here."
            : liveSong is not null
                ? $"The song in {Path.GetFileName(liveSong)} can't be imported, but the presets and samples in its folder can."
                : null);
        nameInput.Text = sourceFolders.Count == 1
            ? Path.GetFileName(sourceFolders[0].TrimEnd(Path.DirectorySeparatorChar))
            : sourcePaths.Count == 1 ? Path.GetFileNameWithoutExtension(sourcePaths[0]) : "Imported Kit";
        sourceCaption.Text = "Looking for sounds…";
        RefreshSources();
        RefreshList(keepSelection: false);
        RefreshSummary();

        var looseFiles = sourcePaths.Where(path => File.Exists(path) && SampleLibrary.IsSupportedFile(path)).Select(Path.GetFullPath);
        var loosePresets = sourcePaths.Where(path => File.Exists(path) && AbletonRackImporter.IsPresetFile(path)).Select(Path.GetFullPath).ToList();
        var foundPaths = await Task.Run(() => SampleLibrary.Scan(sourceFolders, MaximumScannedFiles).Concat(looseFiles).Distinct().ToList(), cancellation);
        var foundPresets = await Task.Run(() => sourceFolders
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            .Where(AbletonRackImporter.IsPresetFile)
            .Concat(loosePresets)
            .Distinct()
            .Take(MaximumScannedFiles)
            .ToList(), cancellation);
        if (cancellation.IsCancellationRequested)
        {
            return;
        }
        rows.AddRange(SoundImportPlanner.SortForKit(foundPaths).Select(path => new SoundRow(path)));
        rows.AddRange(foundPresets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).Select(path => new SoundRow(path)));
        RefreshSources();
        RefreshList(keepSelection: false);

        var readCount = 0;
        await Task.Run(() => Parallel.ForEach(rows.ToList(), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation }, row =>
        {
            try
            {
                if (row.IsPreset)
                {
                    row.Rack = AbletonRackImporter.Read(row.Path, projectBpm);
                    row.Kind = new SoundKind(row.Rack.Kind, 0);
                }
                else
                {
                    row.Seconds = SampleLibrary.Load(row.Path).DurationSeconds;
                }
            }
            catch (Exception readRemediationOpportunity) when (readRemediationOpportunity is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                row.Failure = readRemediationOpportunity.Message;
                row.Included = false;
            }
            var finishedCount = Interlocked.Increment(ref readCount);
            Dispatcher.UIThread.Post(() =>
            {
                // Scanning has ended if this arrives late, and the final count must not be overwritten.
                if (scanning && !cancellation.IsCancellationRequested)
                {
                    sourceCaption.Text = $"Reading {finishedCount} of {rows.Count}…";
                }
            });
        }), CancellationToken.None).ContinueWith(_ => { }, CancellationToken.None);
        if (cancellation.IsCancellationRequested)
        {
            return;
        }

        scanning = false;
        var unreadableCount = rows.Count(row => row.Failure is not null);
        var presetCount = rows.Count(row => row.IsPreset);
        var soundCount = rows.Count - presetCount;
        sourceCaption.Text = (presetCount > 0 ? $"{presetCount} preset{(presetCount == 1 ? string.Empty : "s")} and " : string.Empty) +
            $"{soundCount} sound{(soundCount == 1 ? string.Empty : "s")} found" +
            (unreadableCount > 0 ? $", {unreadableCount} unreadable" : string.Empty) +
            (rows.Count >= MaximumScannedFiles ? $" (stopped at {MaximumScannedFiles})" : string.Empty);
        if (rows.Count == 0)
        {
            ShowNotice(noticeCaption.IsVisible ? noticeCaption.Text : "No WAV, AIFF or FLAC files or Ableton presets in there.");
        }
        else if (!noticeCaption.IsVisible && rows.FirstOrDefault(row => row.IsPreset && row.Failure is not null) is { } unreadablePreset)
        {
            // Saying why matters more for a preset than a sound: someone chose that instrument on purpose.
            ShowNotice(unreadablePreset.Failure);
        }
        if (!modeChosenByHand)
        {
            var readableRows = rows.Where(row => row.Seconds is not null).ToList();
            SelectMode(rows.Any(row => row.Rack is not null)
                ? SoundImportMode.AbletonPreset
                : readableRows.Count > 0 && readableRows.Average(row => row.Seconds!.Value) > DrumKitLongestAverageSeconds
                    ? SoundImportMode.SeparateTracks
                    : SoundImportMode.DrumKit);
        }
        RefreshList(keepSelection: false);
    }

    // ── Refreshing ────────────────────────────────────────────────────────────

    private void ShowNotice(string? notice)
    {
        noticeCaption.Text = notice;
        noticeCaption.IsVisible = notice is not null;
    }

    private void RefreshSources()
    {
        var hasSomething = scanning || rows.Count > 0;
        dropZone.IsVisible = !hasSomething;
        loadedPanel.IsVisible = hasSomething;
    }

    private void SetVisibleIncluded(bool included)
    {
        foreach (var row in VisibleRows().Where(row => row.Failure is null))
        {
            row.Included = included;
        }
        RefreshList(keepSelection: true);
    }

    /// <summary>Presets on the preset card, sounds on the others: each card lists what it can bring in.</summary>
    private IEnumerable<SoundRow> ModeRows() =>
        rows.Where(row => row.IsPreset == (chosenMode == SoundImportMode.AbletonPreset));

    private IEnumerable<SoundRow> VisibleRows()
    {
        var filter = filterInput.Text?.Trim();
        var modeRows = ModeRows();
        return string.IsNullOrEmpty(filter)
            ? modeRows
            : modeRows.Where(row => row.Path.Contains(filter, StringComparison.OrdinalIgnoreCase) || row.Kind.Label.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshList(bool keepSelection)
    {
        RefreshKitKeys();
        var selectedRow = keepSelection ? soundList.SelectedItem : null;
        soundList.ItemsSource = VisibleRows().ToList();
        if (selectedRow is not null)
        {
            soundList.SelectedItem = selectedRow;
        }
        RefreshSummary();
    }

    /// <summary>In Drum kit mode every ticked row shows the key it will land on.</summary>
    private void RefreshKitKeys()
    {
        var kitPosition = 0;
        foreach (var row in rows.Where(row => !row.IsPreset))
        {
            row.KitKey = chosenMode == SoundImportMode.DrumKit && row.Included && kitPosition < SoundImportPlanner.MaximumKitSounds
                ? SoundImportPlanner.KeyForKitPosition(kitPosition++)
                : null;
        }
    }

    private void SelectMode(SoundImportMode mode)
    {
        chosenMode = mode;
        // Only a kit needs naming: presets bring their own, and the rest keep their files' names.
        nameCaption.IsVisible = mode == SoundImportMode.DrumKit;
        nameInput.IsVisible = mode == SoundImportMode.DrumKit;
        foreach (var (cardMode, card) in modeCards)
        {
            card.IsVisible = cardMode != SoundImportMode.AbletonPreset || mode == cardMode || rows.Any(row => row.IsPreset);
            card.BorderBrush = cardMode == mode ? StudioTheme.Accent : StudioTheme.Border;
            card.Background = cardMode == mode ? StudioTheme.AccentSoft : StudioTheme.Surface;
        }
        RefreshList(keepSelection: true);
    }

    private void RefreshSummary()
    {
        if (modeCards.Count > 0 && modeCards[chosenMode].BorderBrush is null)
        {
            SelectMode(chosenMode);
            return;
        }
        var includedCount = ModeRows().Count(row => row.Included);
        var name = nameInput.Text?.Trim();
        var kitCount = Math.Min(includedCount, SoundImportPlanner.MaximumKitSounds);
        summaryCaption.Text = rows.Count == 0
            ? "Drop some sounds on the left to begin."
            : includedCount == 0 && chosenMode != SoundImportMode.SampleLibrary
            ? "Tick the sounds you want."
            : chosenMode switch
            {
                SoundImportMode.AbletonPreset => DescribeIncludedPresets(),
                SoundImportMode.DrumKit => $"{kitCount} sound{(kitCount == 1 ? string.Empty : "s")} on one track, keys {SoundImportPlanner.KeyForKitPosition(0)} to {SoundImportPlanner.KeyForKitPosition(Math.Max(0, kitCount - 1))}." +
                    (includedCount > kitCount ? $" Only the first {kitCount} fit in a kit." : string.Empty),
                SoundImportMode.SeparateTracks => $"{includedCount} new track{(includedCount == 1 ? string.Empty : "s")}.",
                _ => sourceFolders.Count > 0
                    ? $"{sourceFolders.Count} folder{(sourceFolders.Count == 1 ? string.Empty : "s")} added to Browser → Samples."
                    : "The folders holding these files join Browser → Samples.",
            };
        importButton.Content = chosenMode switch
        {
            SoundImportMode.DrumKit => $"Create kit ({kitCount})",
            SoundImportMode.AbletonPreset => $"Import {includedCount} preset{(includedCount == 1 ? string.Empty : "s")}",
            SoundImportMode.SeparateTracks => $"Add {includedCount} track{(includedCount == 1 ? string.Empty : "s")}",
            _ => "Add to library",
        };
        importButton.IsEnabled = !scanning && rows.Count > 0 &&
            (chosenMode == SoundImportMode.SampleLibrary || (includedCount > 0 && (chosenMode != SoundImportMode.DrumKit || !string.IsNullOrWhiteSpace(name))));
    }

    private string DescribeIncludedPresets()
    {
        var racks = ModeRows().Where(row => row.Included && row.Rack is not null).Select(row => row.Rack!).ToList();
        var recordingCount = racks.Sum(rack => rack.Zones.Count);
        var description = $"{racks.Count} new track{(racks.Count == 1 ? string.Empty : "s")}{(recordingCount > 0 ? $" with {recordingCount} recordings" : string.Empty)}.";
        var missing = racks.Sum(rack => rack.MissingFiles.Count);
        if (missing > 0)
        {
            description += $" {missing} recording{(missing == 1 ? " was" : "s were")} not found.";
        }
        var leftOut = racks.SelectMany(rack => rack.LeftOut).Distinct().ToList();
        var effectCount = racks.Sum(rack => rack.Effects.Count);
        if (effectCount > 0)
        {
            description += $" {effectCount} effect{(effectCount == 1 ? string.Empty : "s")} brought over as slaw's own.";
        }
        return leftOut.Count > 0 ? $"{description} Not brought over: {string.Join(", ", leftOut)}." : description;
    }

    private void ConfirmImport()
    {
        var includedPaths = ModeRows().Where(row => row.Included).Select(row => row.Path).ToList();
        if (chosenMode == SoundImportMode.DrumKit)
        {
            includedPaths = includedPaths.Take(SoundImportPlanner.MaximumKitSounds).ToList();
        }
        var libraryFolders = sourceFolders.Count > 0
            ? sourceFolders.ToList()
            : rows.Select(row => Path.GetDirectoryName(row.Path)).OfType<string>().Distinct().ToList();
        var includedRacks = ModeRows().Where(row => row.Included && row.Rack is not null).Select(row => row.Rack!).ToList();
        import(new SoundImportRequest(chosenMode, includedPaths, nameInput.Text?.Trim() ?? "Imported Kit", libraryFolders, includedRacks));
        Close();
    }

    private sealed class SoundRow
    {
        public SoundRow(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileNameWithoutExtension(path);
            IsPreset = AbletonRackImporter.IsPresetFile(path);
            Kind = IsPreset ? new SoundKind("Preset", 0) : SoundImportPlanner.Classify(path);
        }

        public string Path { get; }

        public string Name { get; }

        public bool IsPreset { get; }

        public SoundKind Kind { get; set; }

        public ImportedRack? Rack { get; set; }

        /// <summary>What ▶ plays: the file, the recording a preset plays around middle C (its first if nothing is there), or a synth preset's own note.</summary>
        public string PreviewPath =>
            Rack is null ? Path
            : Rack.Zones.Count == 0 ? AbletonRackImporter.WriteSynthPreview(Rack)
            : (Rack.Zones.FirstOrDefault(zone => zone.KeyLow <= 60 && zone.KeyHigh >= 60) ?? Rack.Zones[0]).Sample;

        public double? Seconds { get; set; }

        public string? Failure { get; set; }

        public bool Included { get; set; } = true;

        public string? KitKey { get; set; }
    }
}
