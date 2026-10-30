using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// Binds every symbol of Avalonia's bundled HarfBuzz (libHarfBuzzSharp.so) before
/// anything else can load the system's libharfbuzz.so.0.
///
/// .NET loads native libraries lazily bound and not -Bsymbolic, so each hb_* call the
/// bundled copy makes into itself is resolved on first use, global scope first. FreeType
/// 2.13+ dlopens the system HarfBuzz RTLD_GLOBAL, and FreeType arrives with VST3 plugins
/// (OB-Xf, via fontconfig) and GTK file dialogs (via Pango). From then on any hb_* function
/// the bundled copy had not called yet resolves into the system copy, which is handed the
/// bundled copy's structs: the studio segfaulted in text shaping
/// (hb_shape_full → system hb_ot_layout_language_find_feature) minutes after a plugin
/// or file dialog had opened. Loading the bundled file RTLD_NOW first resolves all of its
/// relocations while only its own symbols exist; .NET's later load of the same file gets
/// the same, already bound, handle.
/// </summary>
internal static class HarfBuzzSymbolPinning
{
    private const int BindAllSymbolsNow = 2; // RTLD_NOW

    [DllImport("libc", EntryPoint = "dlopen")]
    private static extern IntPtr OpenSharedObject(string fileName, int flags);

    /// <summary>Call before Avalonia starts. Returns the pinned path, or null when there is nothing to pin.</summary>
    public static string? PinBundledHarfBuzz()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        return PinOnLinux();
    }

    [SupportedOSPlatform("linux")]
    private static string? PinOnLinux()
    {
        var runtimeIdentifier = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            Architecture.Arm => "linux-arm",
            _ => null,
        };
        // The same places .NET probes: the RID folder of a portable build, then next to the app (RID-specific publish).
        var candidatePaths = new List<string>();
        if (runtimeIdentifier is not null)
        {
            candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", runtimeIdentifier, "native", "libHarfBuzzSharp.so"));
        }
        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "libHarfBuzzSharp.so"));
        foreach (var candidatePath in candidatePaths)
        {
            try
            {
                if (File.Exists(candidatePath) && OpenSharedObject(candidatePath, BindAllSymbolsNow) != IntPtr.Zero)
                {
                    return candidatePath;
                }
            }
            catch (Exception pinningRemediationOpportunity) when (pinningRemediationOpportunity is DllNotFoundException or EntryPointNotFoundException)
            {
                // No dlopen to call: the studio runs exactly as before, with the old risk.
                return null;
            }
        }
        return null;
    }
}
