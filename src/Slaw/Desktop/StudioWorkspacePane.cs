using Avalonia.Controls;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// A main view above the clip and devices panel, with a splitter between them.
/// Both studio windows are built around one of these. A control can only live in
/// one window at a time, so the studio moves the views between panes rather than
/// asking each window to keep its own copy of the org chart.
/// </summary>
internal sealed class StudioWorkspacePane : Grid
{
    private const double MinimumDetailHeight = 120;
    private const double MaximumRememberedDetailHeight = 900;
    private const double SpaceKeptForTheView = 180;
    private const double SplitterThickness = 5;

    private readonly ContentControl viewHost = new();
    private readonly GridSplitter detailSplitter = new() { Background = StudioTheme.Border, ResizeDirection = GridResizeDirection.Rows };
    private readonly Border detailHost = new() { Background = StudioTheme.Background };
    private bool detailShown = true;
    private double rememberedDetailHeight;

    public StudioWorkspacePane(double detailHeight)
    {
        rememberedDetailHeight = Math.Clamp(detailHeight, MinimumDetailHeight, MaximumRememberedDetailHeight);
        RowDefinitions = new RowDefinitions("*,0,0");
        SetRow(detailSplitter, 1);
        SetRow(detailHost, 2);
        Children.Add(viewHost);
        Children.Add(detailSplitter);
        Children.Add(detailHost);
        RefreshDetailRow();
    }

    public Control? View
    {
        get => viewHost.Content as Control;
        set => viewHost.Content = value;
    }

    public Control? Detail
    {
        get => detailHost.Child;
        set
        {
            detailHost.Child = value;
            RefreshDetailRow();
        }
    }

    /// <summary>The panel's height: what it is now, or what it comes back at if it is hidden.</summary>
    public double DetailHeight => RowDefinitions[2].Height.Value > 0 ? RowDefinitions[2].Height.Value : rememberedDetailHeight;

    public void ShowDetail(bool shown)
    {
        detailShown = shown;
        RefreshDetailRow();
    }

    /// <summary>
    /// Grows the panel for the sample editor, but never below what the user dragged
    /// it to, and always leaves the main view some room.
    /// </summary>
    public void GrowDetailTo(double requestedHeight)
    {
        var paneHeight = Bounds.Height > 0 ? Bounds.Height : 800;
        var targetHeight = Math.Min(requestedHeight, Math.Max(200, paneHeight - SpaceKeptForTheView));
        if (RowDefinitions[2].Height.Value > 0 && RowDefinitions[2].Height.Value < targetHeight)
        {
            RowDefinitions[2].Height = new GridLength(targetHeight, GridUnitType.Pixel);
        }
    }

    private void RefreshDetailRow()
    {
        var detailRow = RowDefinitions[2];
        if (detailRow.Height.Value > 0)
        {
            rememberedDetailHeight = detailRow.Height.Value;
        }
        var detailVisible = detailShown && detailHost.Child is not null;
        RowDefinitions[1].Height = new GridLength(detailVisible ? SplitterThickness : 0, GridUnitType.Pixel);
        detailRow.Height = new GridLength(detailVisible ? rememberedDetailHeight : 0, GridUnitType.Pixel);
        detailSplitter.IsVisible = detailVisible;
        detailHost.IsVisible = detailVisible;
    }
}
