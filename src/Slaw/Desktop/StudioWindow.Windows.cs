using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// Ableton's window model: panels stay in fixed places and are shown or hidden,
/// Tab swaps the views, and a second window carries the view the main window is
/// not showing. Nothing floats, because nothing here has asked to be promoted.
/// </summary>
internal sealed partial class StudioWindow
{
    private const double SidePanelSplitterThickness = 5;
    private const double MinimumBrowserWidth = 140;
    private const double MaximumBrowserWidth = 480;
    private const double MinimumAiPanelWidth = 220;
    private const double MaximumAiPanelWidth = 600;

    private readonly ColumnDefinition browserColumn = new();
    private readonly ColumnDefinition browserSplitterColumn = new();
    private readonly ColumnDefinition aiPanelSplitterColumn = new();
    private readonly ColumnDefinition aiPanelColumn = new();
    private readonly GridSplitter browserSplitter = new() { Background = StudioTheme.Border, ResizeDirection = GridResizeDirection.Columns };
    private readonly GridSplitter aiPanelSplitter = new() { Background = StudioTheme.Border, ResizeDirection = GridResizeDirection.Columns };
    private readonly Border browserPanel = new() { Background = StudioTheme.Surface, BorderBrush = StudioTheme.Border, BorderThickness = new Thickness(0, 0, 1, 0) };
    private readonly List<Action> panelSelectorRefreshes = new();
    private readonly StudioWorkspacePane mainPane;
    private StudioSecondWindow? secondWindow;
    private bool mainWindowShowsArrangement;
    private bool studioWindowClosed;

    /// <summary>The window the keyboard is talking to.</summary>
    private Window FocusedStudioWindow => secondWindow is { IsActive: true } ? secondWindow : this;

    /// <summary>What Space, Enter and the arrows act on: the view in the window that has focus.</summary>
    private bool arrangementViewActive => secondWindow is { IsActive: true } ? !mainWindowShowsArrangement : mainWindowShowsArrangement;

    private StudioWorkspacePane DetailPane => secondWindow is not null && preferences.DetailInSecondWindow ? secondWindow.Pane : mainPane;

    private Control ComposePanelColumns()
    {
        var panelColumns = new Grid();
        panelColumns.ColumnDefinitions.Add(browserColumn);
        panelColumns.ColumnDefinitions.Add(browserSplitterColumn);
        panelColumns.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 320 });
        panelColumns.ColumnDefinitions.Add(aiPanelSplitterColumn);
        panelColumns.ColumnDefinitions.Add(aiPanelColumn);

        browserPanel.Child = browserHost;
        panelColumns.Children.Add(browserPanel);
        panelColumns.Children.Add(PlaceInColumn(browserSplitter, 1));
        panelColumns.Children.Add(PlaceInColumn(mainPane, 2));
        panelColumns.Children.Add(PlaceInColumn(aiPanelSplitter, 3));
        panelColumns.Children.Add(PlaceInColumn(aiPanelHost, 4));
        return panelColumns;
    }

    /// <summary>The little panel switches in the status bar, where Live keeps its view selectors.</summary>
    private Control ComposePanelSelectors()
    {
        ToggleButton Selector(string caption, string toolTip, StudioCommand toggleCommand, Func<bool> isOn, Func<bool>? isAvailable = null)
        {
            var selector = new ToggleButton { Content = caption, FontSize = 11, Padding = new Thickness(7, 2), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            ToolTip.SetTip(selector, toolTip);
            selector.Click += (_, _) => ExecuteStudioCommand(toggleCommand);
            panelSelectorRefreshes.Add(() =>
            {
                selector.IsChecked = isOn();
                selector.IsVisible = isAvailable?.Invoke() ?? true;
            });
            return selector;
        }

        return StudioUi.Row(2,
            Selector("Browser", "Show or hide the browser (Ctrl+Alt+B)", StudioCommand.ToggleBrowser, () => !preferences.BrowserHidden),
            Selector("Clip", "Show or hide the clip and devices panel (Ctrl+Alt+L)", StudioCommand.ToggleDetailPanel, () => !preferences.DetailPanelHidden),
            Selector("Mixer", "Show or hide the Session view's mixer strips (Ctrl+Alt+M)", StudioCommand.ToggleSessionMixer, () => !preferences.SessionMixerHidden),
            Selector("AI", "Show or hide the AI co-pilot panel (Ctrl+Alt+I)", StudioCommand.ToggleAiPanel, () => !preferences.AiPanelHidden, () => AiFeaturesEnabled),
            Selector("2nd Window", "Open or close the second window (Ctrl+Shift+W)", StudioCommand.ToggleSecondWindow, () => secondWindow is not null));
    }

    private void TogglePanel(Action flipPreference)
    {
        flipPreference();
        ApplyPanelVisibility();
        PersistWindowLayout();
    }

    private void ApplyPanelVisibility()
    {
        RememberPanelSizes();
        preferences.BrowserPanelWidth = Math.Clamp(preferences.BrowserPanelWidth, MinimumBrowserWidth, MaximumBrowserWidth);
        preferences.AiPanelWidth = Math.Clamp(preferences.AiPanelWidth, MinimumAiPanelWidth, MaximumAiPanelWidth);
        ShowSidePanel(browserColumn, browserSplitterColumn, browserSplitter, browserPanel, !preferences.BrowserHidden, preferences.BrowserPanelWidth, MinimumBrowserWidth, MaximumBrowserWidth);
        ShowSidePanel(aiPanelColumn, aiPanelSplitterColumn, aiPanelSplitter, aiPanelHost, AiFeaturesEnabled && !preferences.AiPanelHidden, preferences.AiPanelWidth, MinimumAiPanelWidth, MaximumAiPanelWidth);
        mainPane.ShowDetail(!preferences.DetailPanelHidden);
        secondWindow?.Pane.ShowDetail(!preferences.DetailPanelHidden);
        foreach (var refreshSelector in panelSelectorRefreshes)
        {
            refreshSelector();
        }
    }

    private static void ShowSidePanel(ColumnDefinition panelColumn, ColumnDefinition splitterColumn, GridSplitter splitter, Control panel, bool shown, double width, double minimumWidth, double maximumWidth)
    {
        // Minimum first: a hidden column must be allowed down to nothing.
        panelColumn.MinWidth = shown ? minimumWidth : 0;
        panelColumn.MaxWidth = maximumWidth;
        panelColumn.Width = new GridLength(shown ? width : 0, GridUnitType.Pixel);
        splitterColumn.Width = new GridLength(shown ? SidePanelSplitterThickness : 0, GridUnitType.Pixel);
        splitter.IsVisible = shown;
        panel.IsVisible = shown;
    }

    /// <summary>Whatever the splitters were dragged to becomes what the panels come back at.</summary>
    private void RememberPanelSizes()
    {
        // Absolute only: before the first layout pass the columns still hold their default of 1*.
        if (browserColumn.Width is { IsAbsolute: true, Value: > 0 })
        {
            preferences.BrowserPanelWidth = browserColumn.Width.Value;
        }
        if (aiPanelColumn.Width is { IsAbsolute: true, Value: > 0 })
        {
            preferences.AiPanelWidth = aiPanelColumn.Width.Value;
        }
        preferences.DetailPanelHeight = DetailPane.DetailHeight;
    }

    /// <summary>Puts each view in the pane it belongs in. Avalonia wants a control out of its old parent first.</summary>
    private void ArrangeWorkspaces()
    {
        Control mainWindowView = mainWindowShowsArrangement ? arrangementView : sessionView;
        Control otherView = mainWindowShowsArrangement ? sessionView : arrangementView;
        var secondPane = secondWindow?.Pane;

        if (mainPane.View != mainWindowView)
        {
            mainPane.View = null;
        }
        if (secondPane is not null && secondPane.View != otherView)
        {
            secondPane.View = null;
        }
        var detailPane = DetailPane;
        foreach (var pane in new[] { mainPane, secondPane })
        {
            if (pane is not null && pane != detailPane)
            {
                pane.Detail = null;
            }
        }

        // A control moved between windows while its old window still has layout queued
        // for it brings the whole studio down ("wrong LayoutManager"), so settle both first.
        if (secondPane is not null)
        {
            mainPane.UpdateLayout();
            secondPane.UpdateLayout();
        }

        mainPane.View = mainWindowView;
        if (secondPane is not null)
        {
            secondPane.View = otherView;
        }
        detailPane.Detail = detailView;
        secondWindow?.Describe(mainWindowShowsArrangement ? "Session" : "Arrangement", preferences.DetailInSecondWindow);
    }

    private void ToggleSecondWindow()
    {
        if (secondWindow is null)
        {
            preferences.SecondWindowOpen = true;
            OpenSecondWindow();
            PersistWindowLayout();
        }
        else
        {
            secondWindow.Close();
        }
    }

    /// <summary>Sends the clip and devices panel to the second window and back, opening that window if it has to.</summary>
    private void ToggleDetailInSecondWindow()
    {
        RememberPanelSizes();
        preferences.DetailInSecondWindow = !preferences.DetailInSecondWindow;
        if (secondWindow is null && preferences.DetailInSecondWindow)
        {
            preferences.SecondWindowOpen = true;
            OpenSecondWindow();
        }
        else
        {
            ArrangeWorkspaces();
            ApplyPanelVisibility();
        }
        PersistWindowLayout();
    }

    private void OpenSecondWindow()
    {
        var openedWindow = new StudioSecondWindow(
            preferences.DetailPanelHeight,
            () => ExecuteStudioCommand(StudioCommand.SwitchView),
            ToggleDetailInSecondWindow);
        if (preferences.SecondWindowBounds is { } rememberedBounds)
        {
            openedWindow.Width = Math.Max(rememberedBounds.Width, openedWindow.MinWidth);
            openedWindow.Height = Math.Max(rememberedBounds.Height, openedWindow.MinHeight);
            var rememberedPosition = new PixelPoint(rememberedBounds.X, rememberedBounds.Y);
            if (SecondWindowPlacement.IsReachable(rememberedPosition, Screens.All.Select(screen => screen.WorkingArea)))
            {
                openedWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                openedWindow.Position = rememberedPosition;
            }
        }
        WireStudioInput(openedWindow);
        openedWindow.Closing += (_, _) =>
        {
            preferences.SecondWindowBounds = new StudioWindowBounds(openedWindow.Position.X, openedWindow.Position.Y, openedWindow.Bounds.Width, openedWindow.Bounds.Height);
            RememberPanelSizes();
        };
        openedWindow.Closed += (_, _) => HandleSecondWindowClosed(openedWindow);
        secondWindow = openedWindow;
        ArrangeWorkspaces();
        ApplyPanelVisibility();
        openedWindow.Show();
    }

    private void HandleSecondWindowClosed(StudioSecondWindow closedWindow)
    {
        closedWindow.Pane.View = null;
        closedWindow.Pane.Detail = null;
        secondWindow = null;
        if (studioWindowClosed)
        {
            // Quitting with it open: it comes back next time.
            return;
        }
        preferences.SecondWindowOpen = false;
        ArrangeWorkspaces();
        ApplyPanelVisibility();
        PersistWindowLayout();
    }

    private void CloseSecondWindowWithStudio()
    {
        studioWindowClosed = true;
        RememberPanelSizes();
        secondWindow?.Close();
        PersistWindowLayout();
    }

    private void PersistWindowLayout()
    {
        try
        {
            preferences.SaveWindowLayout();
        }
        catch (Exception persistenceRemediationOpportunity) when (persistenceRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            SetStatus(StatusSeverity.Warning, $"window layout could not be saved: {persistenceRemediationOpportunity.Message}");
        }
    }

    private void ToggleFullScreen()
    {
        var focusedWindow = FocusedStudioWindow;
        focusedWindow.WindowState = focusedWindow.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    }
}
