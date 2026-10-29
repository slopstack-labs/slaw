using System.Runtime.InteropServices;
using System.Text;

namespace SlopstackLabs.Slaw.Terminal;

/// <summary>
/// The escape sequences slaw styles its output with. These are properties rather
/// than constants because not every destination can render them: a redirected
/// stream, a NO_COLOR environment, or a Windows console that refuses
/// virtual-terminal processing all want them neutralized to empty strings rather
/// than emitted as literal "\u001b[36m" noise.
/// </summary>
internal static class TerminalPalette
{
    // ── ANSI palette ──────────────────────────────────────────────────────────

    public static string Reset { get; private set; } = "\u001b[0m";
    public static string Bold { get; private set; } = "\u001b[1m";
    public static string Dim { get; private set; } = "\u001b[2m";
    public static string Green { get; private set; } = "\u001b[32m";
    public static string Cyan { get; private set; } = "\u001b[36m";
    public static string Magenta { get; private set; } = "\u001b[35m";
    public static string Yellow { get; private set; } = "\u001b[33m";
    public static string Red { get; private set; } = "\u001b[31m";

    /// <summary>
    /// Returns the cursor to column zero and erases the line, so a spinner frame
    /// can overwrite its predecessor in place.
    /// </summary>
    public static string ClearLine { get; private set; } = "\r\u001b[K";

    // ── Glyphs ────────────────────────────────────────────────────────────────

    // Downgraded to ASCII when the console cannot be switched to UTF-8 output.
    // A legacy Windows code page renders "✓" as a question mark, which reads as a
    // failure rather than a success.
    public static string GlyphOK { get; private set; } = "✓";
    public static string GlyphFail { get; private set; } = "✗";
    public static string GlyphWarn { get; private set; } = "⚠";
    public static string GlyphRetry { get; private set; } = "↻";
    public static string GlyphIterate { get; private set; } = "⟳";
    public static string GlyphNote { get; private set; } = "♪";

    public static IReadOnlyList<string> SpinnerAnimationFrames { get; private set; } =
        new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    /// <summary>
    /// Whether the spinner may redraw in place. Without ANSI support an
    /// "animation" is just thousands of duplicated log lines, so the spinner
    /// degrades to printing one settled line per step instead.
    /// </summary>
    public static bool AnimationsEnabled { get; private set; } = true;

    /// <summary>
    /// Whether the attached console can render cursor addressing at all. The
    /// studio refuses to launch without it rather than painting escape soup.
    /// </summary>
    public static bool VirtualTerminalSupported { get; private set; } = true;

    static TerminalPalette()
    {
        NegotiateTerminalCapabilities();
    }

    /// <summary>
    /// Forces the static constructor to run, so capability negotiation happens
    /// before the first byte reaches the console rather than lazily mid-frame.
    /// </summary>
    public static void EnsureNegotiated()
    {
    }

    private static void NegotiateTerminalCapabilities()
    {
        var (ansiSupported, utf8Supported) = EnablePlatformTerminalFeatures();
        VirtualTerminalSupported = ansiSupported;

        if (!ansiSupported || Console.IsErrorRedirected || ColorSuppressedByEnvironment())
        {
            DisableAnsiStyling();
        }
        if (!utf8Supported)
        {
            UseAsciiGlyphs();
        }
    }

    /// <summary>
    /// Honours the two conventions users reach for when they want plain output:
    /// the NO_COLOR standard and TERM=dumb.
    /// </summary>
    private static bool ColorSuppressedByEnvironment()
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            return true;
        }
        return string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
    }

    private static void DisableAnsiStyling()
    {
        Reset = Bold = Dim = Green = Cyan = Magenta = Yellow = Red = string.Empty;
        ClearLine = string.Empty;
        AnimationsEnabled = false;
    }

    private static void UseAsciiGlyphs()
    {
        GlyphOK = "OK";
        GlyphFail = "XX";
        GlyphWarn = "!!";
        GlyphRetry = "->";
        GlyphIterate = "=>";
        GlyphNote = "~";
        SpinnerAnimationFrames = new[] { "|", "/", "-", "\\" };
    }

    // ── Platform negotiation ──────────────────────────────────────────────────

    /// <summary>
    /// Unix terminals speak ANSI and UTF-8 natively. Windows consoles have to be
    /// asked: virtual-terminal processing is opt-in per handle, and the output
    /// code page defaults to whatever the OEM shipped in 1993.
    /// </summary>
    private static (bool AnsiSupported, bool Utf8Supported) EnablePlatformTerminalFeatures()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (true, true);
        }

        var utf8NegotiationOutcome = true;
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            utf8NegotiationOutcome = false;
        }

        var ansiNegotiationOutcome =
            EnableWindowsVirtualTerminalProcessing(StandardErrorHandleIdentifier) |
            EnableWindowsVirtualTerminalProcessing(StandardOutputHandleIdentifier);
        return (ansiNegotiationOutcome, utf8NegotiationOutcome);
    }

    private const int StandardOutputHandleIdentifier = -11;
    private const int StandardErrorHandleIdentifier = -12;
    private const uint EnableVirtualTerminalProcessingFlag = 0x0004;

    private static bool EnableWindowsVirtualTerminalProcessing(int standardHandleIdentifier)
    {
        var consoleHandle = GetStdHandle(standardHandleIdentifier);
        if (consoleHandle == IntPtr.Zero || consoleHandle == new IntPtr(-1))
        {
            return false;
        }
        if (!GetConsoleMode(consoleHandle, out var existingConsoleMode))
        {
            return false;
        }
        return SetConsoleMode(consoleHandle, existingConsoleMode | EnableVirtualTerminalProcessingFlag);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandleIdentifier);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr consoleHandle, out uint consoleMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr consoleHandle, uint consoleMode);
}
