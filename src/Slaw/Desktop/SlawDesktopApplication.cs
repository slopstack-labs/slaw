using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>Everything the studio window needs to know at launch.</summary>
internal sealed record StudioLaunchParameters(
    SlawProject Project,
    string? ProjectFilePath,
    StudioPreferences Preferences,
    bool OpenInArrangementView = false);

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
        return AppBuilder.Configure<SlawDesktopApplication>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(rawArguments);
    }

    public override void Initialize()
    {
        var fluentThemeSubstrate = new FluentTheme();
        fluentThemeSubstrate.Palettes[ThemeVariant.Dark] = new ColorPaletteResources
        {
            Accent = StudioTheme.AccentColor,
            RegionColor = StudioTheme.BackgroundColor,
            AltHigh = StudioTheme.BackgroundColor,
            ChromeMediumLow = StudioTheme.SurfaceColor,
            ChromeMedium = StudioTheme.SurfaceRaisedColor,
            BaseHigh = Colors.White,
        };
        Styles.Add(fluentThemeSubstrate);
        RequestedThemeVariant = ThemeVariant.Dark;
        Name = "slaw";
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
