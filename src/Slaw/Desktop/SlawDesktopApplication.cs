using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>Everything the studio window needs to know at launch.</summary>
internal sealed record StudioLaunchParameters(
    SlawProject Project,
    string? ProjectFilePath,
    StudioPreferences Preferences,
    bool OpenInArrangementView = false,
    bool OpenSampleEditor = false);

/// <summary>
/// The Avalonia application host. The interface is composed entirely in C#:
/// declarative markup is a legacy constraint inherited from a less agentic era.
/// </summary>
internal sealed class SlawDesktopApplication : Application
{
    private static StudioLaunchParameters? pendingLaunchParameters;

    public static int Launch(StudioLaunchParameters launchParameters, string[] rawArguments)
    {
        pendingLaunchParameters = launchParameters;
        // Before Avalonia touches text: plugins and file dialogs load the system HarfBuzz later.
        HarfBuzzSymbolPinning.PinBundledHarfBuzz();
        return AppBuilder.Configure<SlawDesktopApplication>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(rawArguments);
    }

    private readonly ColorPaletteResources darkPalette = new();
    private readonly ColorPaletteResources lightPalette = new();

    public override void Initialize()
    {
        var fluentThemeSubstrate = new FluentTheme();
        fluentThemeSubstrate.Palettes[ThemeVariant.Dark] = darkPalette;
        fluentThemeSubstrate.Palettes[ThemeVariant.Light] = lightPalette;
        Styles.Add(fluentThemeSubstrate);
        StudioStyles.Apply(this);
        Name = "slaw";
        ApplyFluentPalette(StudioTheme.Current);
    }

    /// <summary>
    /// Switches the whole studio to <paramref name="theme"/>: slaw's brushes (recoloured in place), Fluent's
    /// palette for the controls slaw does not style itself, and Fluent's light or dark variant. Every open
    /// window is repainted. Works without a running application too (tests), where only the brushes change.
    /// </summary>
    public static void ApplyTheme(StudioThemeDefinition theme)
    {
        StudioTheme.Apply(theme);
        if (Current is not SlawDesktopApplication application)
        {
            return;
        }
        application.ApplyFluentPalette(theme);
        if (application.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            foreach (var openWindow in desktopLifetime.Windows)
            {
                // Custom-drawn controls read their brushes in Render; they only need asking to draw again.
                openWindow.InvalidateVisual();
                foreach (var descendant in openWindow.GetVisualDescendants())
                {
                    descendant.InvalidateVisual();
                }
            }
        }
    }

    private void ApplyFluentPalette(StudioThemeDefinition theme)
    {
        var palette = theme.IsLight ? lightPalette : darkPalette;
        palette.Accent = theme.Colors[StudioThemeSlots.Accent];
        palette.RegionColor = theme.Colors[StudioThemeSlots.Background];
        palette.AltHigh = theme.Colors[StudioThemeSlots.Background];
        palette.ChromeMediumLow = theme.Colors[StudioThemeSlots.Surface];
        palette.ChromeMedium = theme.Colors[StudioThemeSlots.SurfaceRaised];
        palette.BaseHigh = theme.Colors[StudioThemeSlots.Text];
        RequestedThemeVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime && pendingLaunchParameters is not null)
        {
            desktopLifetime.MainWindow = new StudioWindow(pendingLaunchParameters);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
