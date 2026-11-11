using Avalonia.Media;
using SlopstackLabs.Slaw.Desktop;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>Themes: built-ins, theme files (partial, broken, light), templates, and recolouring the live brushes.</summary>
public sealed class ThemeTests : IDisposable
{
    private readonly string themesFolder = Path.Combine(Path.GetTempPath(), $"slaw-themes-{Guid.NewGuid():N}");

    public ThemeTests() => Directory.CreateDirectory(themesFolder);

    public void Dispose() => Directory.Delete(themesFolder, recursive: true);

    [Fact]
    public void EveryBuiltInThemeFillsEverySlot()
    {
        Assert.Equal(StudioThemeCatalog.DefaultThemeName, StudioThemeCatalog.BuiltIn[0].Name);
        foreach (var theme in StudioThemeCatalog.BuiltIn)
        {
            Assert.All(StudioThemeSlots.All, slot => Assert.True(theme.Colors.ContainsKey(slot.Key), $"{theme.Name} has no {slot.Key}"));
            Assert.NotEmpty(theme.TrackColors);
        }
        Assert.Contains(StudioThemeCatalog.BuiltIn, theme => theme.IsLight);
    }

    [Fact]
    public void APartialThemeFileTakesTheRestFromItsBase()
    {
        File.WriteAllText(Path.Combine(themesFolder, "mint.json"), """
            {
              // comments and trailing commas are fine
              "name": "Mint Condition",
              "base": "light",
              "colors": { "accent": "#10b981", },
              "trackColors": ["#ff0000", "#00ff00"],
            }
            """);

        var (themes, problems) = StudioThemeCatalog.Load(themesFolder);

        Assert.Empty(problems);
        var mint = StudioThemeCatalog.Find(themes, "mint condition");
        Assert.Equal("Mint Condition", mint.Name);
        Assert.True(mint.IsLight);
        Assert.False(mint.IsBuiltIn);
        Assert.Equal(Color.Parse("#10b981"), mint.Colors[StudioThemeSlots.Accent]);
        Assert.Equal(StudioThemeCatalog.OpenPlanOffice.Colors[StudioThemeSlots.Background], mint.Colors[StudioThemeSlots.Background]);
        Assert.Equal(new[] { Color.Parse("#ff0000"), Color.Parse("#00ff00") }, mint.TrackColors);
        Assert.Equal(StudioThemeCatalog.BuiltIn.Count + 1, themes.Count);
    }

    [Fact]
    public void ProblemsInThemeFilesAreReportedAndNeverFatal()
    {
        File.WriteAllText(Path.Combine(themesFolder, "typos.json"), """
            { "name": "Typos", "base": "sepia", "colours": {}, "colors": { "bakground": "#000000", "text": "white-ish", "accent": "#123456" } }
            """);
        File.WriteAllText(Path.Combine(themesFolder, "broken.json"), "{ not json");

        var (themes, problems) = StudioThemeCatalog.Load(themesFolder);

        var typos = StudioThemeCatalog.Find(themes, "Typos");
        Assert.Equal(Color.Parse("#123456"), typos.Colors[StudioThemeSlots.Accent]);
        Assert.Equal(StudioThemeCatalog.Default.Colors[StudioThemeSlots.Text], typos.Colors[StudioThemeSlots.Text]);
        Assert.False(typos.IsLight);
        var messages = problems.Select(problem => problem.ToString()).ToList();
        Assert.Contains(messages, message => message.StartsWith("typos.json:") && message.Contains("base \"sepia\""));
        Assert.Contains(messages, message => message.Contains("unknown key \"colours\""));
        Assert.Contains(messages, message => message.Contains("unknown colour \"bakground\""));
        Assert.Contains(messages, message => message.Contains("\"text\": \"white-ish\" is not a colour"));
        Assert.Contains(messages, message => message.StartsWith("broken.json: not loaded"));
        Assert.DoesNotContain(themes, theme => theme.FilePath?.EndsWith("broken.json") == true);
    }

    [Fact]
    public void ATemplateSpellsOutEverySlotAndLoadsBackUnchanged()
    {
        var firstFile = StudioThemeCatalog.WriteTemplate(StudioThemeCatalog.AfterHours, "Night Shift", themesFolder);
        var secondFile = StudioThemeCatalog.WriteTemplate(StudioThemeCatalog.AfterHours, "Night Shift", themesFolder);

        Assert.NotEqual(firstFile, secondFile);
        var templateText = File.ReadAllText(firstFile);
        Assert.All(StudioThemeSlots.All, slot => Assert.Contains($"\"{slot.Key}\"", templateText));
        var (themes, problems) = StudioThemeCatalog.Load(themesFolder);
        Assert.Empty(problems);
        var nightShift = StudioThemeCatalog.Find(themes, "Night Shift");
        Assert.Equal(StudioThemeCatalog.AfterHours.Colors, nightShift.Colors);
        Assert.Equal(StudioThemeCatalog.AfterHours.TrackColors, nightShift.TrackColors);
        Assert.Equal("Night Shift 2", StudioThemeCatalog.Find(themes, "Night Shift 2").Name);
    }

    [Fact]
    public void AnUnknownThemeNameFallsBackToTheDefault()
    {
        Assert.Same(StudioThemeCatalog.Default, StudioThemeCatalog.Find(StudioThemeCatalog.BuiltIn, "Synergy Beige"));
        Assert.Same(StudioThemeCatalog.Default, StudioThemeCatalog.Find(StudioThemeCatalog.BuiltIn, null));
    }

    [Fact]
    public void ApplyingAThemeRecoloursTheBrushesInPlace()
    {
        var textBrush = (ISolidColorBrush)StudioTheme.Text;
        var gridTint = StudioTheme.Tint(StudioThemeSlots.Border, 0.5);
        var firstTrack = (ISolidColorBrush)StudioTheme.TrackBrush(0);
        try
        {
            StudioTheme.Apply(StudioThemeCatalog.OpenPlanOffice);

            Assert.Same(textBrush, StudioTheme.Text);
            Assert.Equal(StudioThemeCatalog.OpenPlanOffice.Colors[StudioThemeSlots.Text], textBrush.Color);
            Assert.Equal(StudioThemeCatalog.OpenPlanOffice.Colors[StudioThemeSlots.Border], gridTint.Color);
            Assert.Equal(0.5, gridTint.Opacity);
            Assert.Equal(StudioThemeCatalog.OpenPlanOffice.TrackColors[0], firstTrack.Color);
            Assert.Equal(StudioThemeCatalog.OpenPlanOffice.Colors[StudioThemeSlots.Accent], StudioTheme.AccentColor);
        }
        finally
        {
            StudioTheme.Apply(StudioThemeCatalog.Default);
        }
        Assert.Equal(StudioThemeCatalog.Default.Colors[StudioThemeSlots.Text], textBrush.Color);
    }
}
