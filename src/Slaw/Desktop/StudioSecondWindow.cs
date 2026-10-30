using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// Live's second window: whichever of Session and Arrangement the main window is
/// not showing, ready for the other monitor, and the clip and devices panel if you
/// send it over. It holds no state of its own. The studio owns the views, the keys
/// and the transport, and this window is merely additional headcount.
/// </summary>
internal sealed class StudioSecondWindow : Window
{
    private readonly TextBlock viewCaption = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = StudioTheme.Text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private readonly ToggleButton detailHereToggle = new() { Content = "Clip & Devices here", FontSize = 12, Padding = new Thickness(7, 3) };

    public StudioSecondWindow(double detailHeight, Action swapViews, Action toggleDetailHere)
    {
        Title = "slaw — second window";
        Width = 1200;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        Background = StudioTheme.Background;

        Pane = new StudioWorkspacePane(detailHeight);
        ToolTip.SetTip(detailHereToggle, "Keep the clip and devices panel in this window instead of the main one");
        detailHereToggle.Click += (_, _) => toggleDetailHere();

        var controls = StudioUi.Row(6,
            detailHereToggle,
            StudioUi.Button("⇄ Swap with main window", swapViews, "Swap the views of the two windows (Tab)", compact: true));
        DockPanel.SetDock(controls, Dock.Right);
        var headerBar = new Border
        {
            Background = StudioTheme.Surface,
            BorderBrush = StudioTheme.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 5),
            Child = new DockPanel { Children = { controls, viewCaption } },
        };
        DockPanel.SetDock(headerBar, Dock.Top);
        Content = new DockPanel { Children = { headerBar, Pane } };
    }

    public StudioWorkspacePane Pane { get; }

    public void Describe(string viewName, bool detailHere)
    {
        Title = $"slaw — {viewName}";
        viewCaption.Text = viewName;
        detailHereToggle.IsChecked = detailHere;
    }
}

/// <summary>Where the second window goes when it comes back. The monitor it was on may not have.</summary>
internal static class SecondWindowPlacement
{
    /// <summary>Whether the top of a window at this position, where the title bar is, lands on a screen someone can reach.</summary>
    public static bool IsReachable(PixelPoint windowPosition, IEnumerable<PixelRect> screenWorkingAreas)
    {
        const int TitleBarGrabOffset = 40;
        var titleBarPoint = new PixelPoint(windowPosition.X + TitleBarGrabOffset, windowPosition.Y + TitleBarGrabOffset / 2);
        return screenWorkingAreas.Any(workingArea => workingArea.Contains(titleBarPoint));
    }
}
