using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SlopstackLabs.Slaw.Desktop;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// The studio segfaulted in text shaping after a VST3 plugin's FreeType (or a GTK file dialog)
/// had loaded the system HarfBuzz globally. A regression here crashes the test host rather than
/// failing politely, which is at least hard to miss.
/// </summary>
public class HarfBuzzSymbolPinningTests
{
    [DllImport("libc", EntryPoint = "dlopen")]
    private static extern IntPtr OpenSharedObject(string fileName, int flags);

    [Fact]
    public void Bundled_harfbuzz_still_shapes_text_after_the_system_harfbuzz_is_loaded_globally()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        Assert.NotNull(HarfBuzzSymbolPinning.PinBundledHarfBuzz());
        var fontPath = new[] { "/usr/share/fonts", "/usr/local/share/fonts" }
            .Where(Directory.Exists)
            .SelectMany(fontFolder => Directory.EnumerateFiles(fontFolder, "*.ttf", SearchOption.AllDirectories))
            .FirstOrDefault();
        // What FreeType 2.13+ does on first use: RTLD_LAZY | RTLD_GLOBAL.
        if (fontPath is null || OpenSharedObject("libharfbuzz.so.0", 0x1 | 0x100) == IntPtr.Zero)
        {
            return; // no system HarfBuzz or no font: nothing can clash on this machine
        }

        using var fontBlob = Blob.FromFile(fontPath);
        using var fontFace = new Face(fontBlob, 0);
        using var shapedFont = new Font(fontFace);
        using var shapingBuffer = new HarfBuzzSharp.Buffer();
        shapingBuffer.AddUtf8("slaw synergy");
        shapingBuffer.GuessSegmentProperties();
        shapedFont.Shape(shapingBuffer);

        Assert.Equal(12, shapingBuffer.Length);
    }
}
