using System.Globalization;
using System.Text;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// The studio draws its icons as vectors (StudioIcons). Emoji and symbol glyphs that the bundled
/// Inter font lacks fall back to the system emoji font, which on some systems is not drawn until
/// the control repaints (hover). This keeps them out of the Desktop sources.
/// </summary>
public sealed class StudioIconTests
{
    // Typography Inter has and the UI uses on purpose: dashes, arrows in prose, the ellipsis, dots, maths.
    private static readonly HashSet<int> AllowedSymbols = "…·→←—–×−÷∞±“”‘’µ°♯♭─│┌┐└┘├┤▲▼●○▶◀■✓✗↑↓⇧⌘⌥"
        .EnumerateRunes().Select(rune => rune.Value).ToHashSet();

    [Fact]
    public void DesktopSourcesUseNoEmojiOrFallbackSymbolGlyphs()
    {
        var desktopFolder = Path.Combine(FindRepositoryRoot(), "src", "Slaw", "Desktop");
        var offenders = new List<string>();
        foreach (var sourceFile in Directory.EnumerateFiles(desktopFolder, "*.cs"))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(sourceFile))
            {
                lineNumber++;
                foreach (var rune in line.EnumerateRunes())
                {
                    if (IsPictographic(rune) && !AllowedSymbols.Contains(rune.Value))
                    {
                        offenders.Add($"{Path.GetFileName(sourceFile)}:{lineNumber} U+{rune.Value:X4} {rune}");
                    }
                }
            }
        }
        Assert.True(offenders.Count == 0, "use a StudioIcon instead of these glyphs:\n" + string.Join("\n", offenders));
    }

    private static bool IsPictographic(Rune rune) =>
        rune.Value >= 0x1F000 // emoji planes
        || (rune.Value >= 0x2190 && rune.Value <= 0x2BFF) // arrows, technical, box/block shapes, dingbats, misc symbols
        || (rune.Value >= 0xFF01 && rune.Value <= 0xFF5E) // full-width forms ("＋")
        || Rune.GetUnicodeCategory(rune) == UnicodeCategory.OtherSymbol && rune.Value > 0x7F && rune.Value != 0xB0 && rune.Value != 0xA9 && rune.Value != 0xAE;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Slaw", "Desktop")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("could not find the repository root above " + AppContext.BaseDirectory);
    }
}
