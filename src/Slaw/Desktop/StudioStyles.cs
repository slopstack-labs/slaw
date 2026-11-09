using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The studio's control styling on top of Fluent: theme resources for buttons, toggles and
/// inputs, plus the few styles resources cannot express (icon buttons, AI buttons, browser rows).
/// Every colour comes from <see cref="StudioTheme"/>, so a theme only has to change that.
/// </summary>
internal static class StudioStyles
{
    public static void Apply(Application application)
    {
        var resources = application.Resources;
        resources["ControlCornerRadius"] = new CornerRadius(6);
        resources["OverlayCornerRadius"] = new CornerRadius(8);

        resources["ButtonBackground"] = StudioTheme.SurfaceRaised;
        resources["ButtonBackgroundPointerOver"] = StudioTheme.SurfaceHover;
        resources["ButtonBackgroundPressed"] = StudioTheme.SurfacePressed;
        resources["ButtonBackgroundDisabled"] = StudioTheme.Surface;
        resources["ButtonBorderBrush"] = StudioTheme.Border;
        resources["ButtonBorderBrushPointerOver"] = StudioTheme.BorderStrong;
        resources["ButtonBorderBrushPressed"] = StudioTheme.BorderStrong;
        resources["ButtonBorderBrushDisabled"] = StudioTheme.Border;
        resources["ButtonForeground"] = StudioTheme.Text;
        resources["ButtonForegroundPointerOver"] = StudioTheme.Text;
        resources["ButtonForegroundPressed"] = StudioTheme.Text;
        resources["ButtonForegroundDisabled"] = StudioTheme.TextDim;
        resources["ButtonBorderThemeThickness"] = new Thickness(1);

        resources["ToggleButtonBackground"] = StudioTheme.SurfaceRaised;
        resources["ToggleButtonBackgroundPointerOver"] = StudioTheme.SurfaceHover;
        resources["ToggleButtonBackgroundPressed"] = StudioTheme.SurfacePressed;
        resources["ToggleButtonBorderBrush"] = StudioTheme.Border;
        resources["ToggleButtonBorderBrushPointerOver"] = StudioTheme.BorderStrong;
        resources["ToggleButtonForeground"] = StudioTheme.Text;
        resources["ToggleButtonForegroundPointerOver"] = StudioTheme.Text;

        resources["TextControlBackground"] = StudioTheme.Background;
        resources["TextControlBackgroundPointerOver"] = StudioTheme.Background;
        resources["TextControlBackgroundFocused"] = StudioTheme.Background;
        resources["TextControlBorderBrush"] = StudioTheme.Border;
        resources["TextControlBorderBrushPointerOver"] = StudioTheme.BorderStrong;
        resources["TextControlPlaceholderForeground"] = StudioTheme.TextMuted;

        resources["ComboBoxBackground"] = StudioTheme.SurfaceRaised;
        resources["ComboBoxBackgroundPointerOver"] = StudioTheme.SurfaceHover;
        resources["ComboBoxBorderBrush"] = StudioTheme.Border;
        resources["ComboBoxBorderBrushPointerOver"] = StudioTheme.BorderStrong;
        resources["ComboBoxDropDownBackground"] = StudioTheme.SurfaceRaised;
        resources["ComboBoxDropDownBorderBrush"] = StudioTheme.BorderStrong;

        resources["MenuFlyoutPresenterBackground"] = StudioTheme.SurfaceRaised;
        resources["MenuFlyoutPresenterBorderBrush"] = StudioTheme.BorderStrong;
        resources["ToolTipBackground"] = StudioTheme.SurfaceRaised;
        resources["ToolTipBorderBrush"] = StudioTheme.BorderStrong;
        resources["ToolTipForeground"] = StudioTheme.Text;

        application.Styles.Add(new Style(selector => selector.OfType<Button>())
        {
            Setters = { new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(1)) },
        });

        // Icon-only buttons are square and centred.
        application.Styles.Add(new Style(selector => selector.OfType<Button>().Class("icon"))
        {
            Setters =
            {
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                new Setter(Layoutable.MinWidthProperty, 26.0),
                new Setter(Layoutable.MinHeightProperty, 26.0),
            },
        });

        // AI buttons keep their gradient and white text when hovered and pressed.
        foreach (var state in new[] { ":pointerover", ":pressed" })
        {
            application.Styles.Add(new Style(selector => selector.OfType<Button>().Class("ai").Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, StudioTheme.AiGradientHover),
                    new Setter(ContentPresenter.ForegroundProperty, StudioTheme.OnAccent),
                    new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                },
            });
        }
        application.Styles.Add(new Style(selector => selector.OfType<Button>().Class("ai"))
        {
            Setters = { new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent) },
        });

        // Browser rows: flat until hovered.
        application.Styles.Add(new Style(selector => selector.OfType<Button>().Class("browserRow"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderBrushProperty, Brushes.Transparent),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(5)),
            },
        });
        application.Styles.Add(new Style(selector => selector.OfType<Button>().Class("browserRow").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
        {
            Setters =
            {
                new Setter(ContentPresenter.BackgroundProperty, StudioTheme.SurfaceHover),
                new Setter(ContentPresenter.ForegroundProperty, StudioTheme.Text),
            },
        });
    }
}
