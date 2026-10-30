using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// The slice of Xlib and libc that Linux plugin editors need: a top-level
/// window to embed the plugin's view in, its events, and poll() for the run
/// loop. Works under XWayland as well as on a plain X server.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe class X11Windowing
{
    private const string LibX11 = "libX11.so.6";
    private const string LibC = "libc";

    // Event types (X.h).
    public const int DestroyNotify = 17;
    public const int ConfigureNotify = 22;
    public const int ClientMessage = 33;

    public const long StructureNotifyMask = 1L << 17;
    public const long SizeHintMinimum = 1L << 4;
    public const long SizeHintMaximum = 1L << 5;
    public const int PropertyModeReplace = 0;

    /// <summary>XEvent is a union of 24 longs.</summary>
    public const int EventSize = 24 * 8;

    // Offsets into XEvent on LP64 (Xlib.h): type, serial, send_event, display, then per-event fields.
    public const int EventWindowOffset = 32;
    public const int ConfigureWindowOffset = 40;
    public const int ConfigureWidthOffset = 56;
    public const int ConfigureHeightOffset = 60;
    public const int ClientMessageFirstLongOffset = 56;

    public const short PollIn = 0x001;
    public const short PollError = 0x008;
    public const short PollHangUp = 0x010;

    private const int EventFdNonBlocking = 0x800;
    private const int EventFdCloseOnExec = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollDescriptor
    {
        public int FileDescriptor;
        public short RequestedEvents;
        public short ReturnedEvents;
    }

    /// <summary>XSizeHints (Xutil.h) on LP64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SizeHints
    {
        public long Flags;
        public int X, Y, Width, Height;
        public int MinimumWidth, MinimumHeight, MaximumWidth, MaximumHeight;
        public int WidthIncrement, HeightIncrement;
        public int MinimumAspectX, MinimumAspectY, MaximumAspectX, MaximumAspectY;
        public int BaseWidth, BaseHeight, WindowGravity;
    }

    [DllImport(LibX11)] public static extern int XInitThreads();
    [DllImport(LibX11)] public static extern IntPtr XOpenDisplay(IntPtr displayName);
    [DllImport(LibX11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(LibX11)] public static extern int XConnectionNumber(IntPtr display);
    [DllImport(LibX11)] public static extern int XDefaultScreen(IntPtr display);
    [DllImport(LibX11)] public static extern UIntPtr XRootWindow(IntPtr display, int screenNumber);
    [DllImport(LibX11)] public static extern UIntPtr XBlackPixel(IntPtr display, int screenNumber);

    [DllImport(LibX11)]
    public static extern UIntPtr XCreateSimpleWindow(IntPtr display, UIntPtr parent, int x, int y, uint width, uint height,
        uint borderWidth, UIntPtr border, UIntPtr background);

    [DllImport(LibX11)] public static extern int XDestroyWindow(IntPtr display, UIntPtr window);
    [DllImport(LibX11)] public static extern int XSelectInput(IntPtr display, UIntPtr window, long eventMask);
    [DllImport(LibX11)] public static extern int XMapRaised(IntPtr display, UIntPtr window);
    [DllImport(LibX11)] public static extern int XResizeWindow(IntPtr display, UIntPtr window, uint width, uint height);
    [DllImport(LibX11)] public static extern int XFlush(IntPtr display);
    [DllImport(LibX11)] public static extern int XSync(IntPtr display, int discard);
    [DllImport(LibX11)] public static extern int XPending(IntPtr display);
    [DllImport(LibX11)] public static extern int XNextEvent(IntPtr display, byte* eventReturn);
    [DllImport(LibX11)] public static extern UIntPtr XInternAtom(IntPtr display, byte* atomName, int onlyIfExists);
    [DllImport(LibX11)] public static extern int XSetWMProtocols(IntPtr display, UIntPtr window, UIntPtr* protocols, int count);
    [DllImport(LibX11)] public static extern int XStoreName(IntPtr display, UIntPtr window, byte* windowName);
    [DllImport(LibX11)] public static extern void XSetWMNormalHints(IntPtr display, UIntPtr window, SizeHints* hints);
    [DllImport(LibX11)] public static extern int XSetTransientForHint(IntPtr display, UIntPtr window, UIntPtr ownerWindow);

    [DllImport(LibX11)]
    public static extern int XChangeProperty(IntPtr display, UIntPtr window, UIntPtr property, UIntPtr type, int format, int mode,
        byte* data, int elementCount);

    [DllImport(LibC, EntryPoint = "poll", SetLastError = true)] public static extern int PollDescriptors(PollDescriptor* descriptors, ulong descriptorCount, int timeoutMilliseconds);
    [DllImport(LibC, EntryPoint = "eventfd", SetLastError = true)] private static extern int CreateEventDescriptor(uint initialValue, int flags);
    [DllImport(LibC, EntryPoint = "read", SetLastError = true)] private static extern nint ReadDescriptor(int fileDescriptor, void* buffer, nint count);
    [DllImport(LibC, EntryPoint = "write", SetLastError = true)] private static extern nint WriteDescriptor(int fileDescriptor, void* buffer, nint count);

    public static UIntPtr InternAtom(IntPtr display, string atomName)
    {
        var atomBytes = System.Text.Encoding.ASCII.GetBytes(atomName + "\0");
        fixed (byte* atomPointer = atomBytes)
        {
            return XInternAtom(display, atomPointer, 0);
        }
    }

    /// <summary>Sets both the legacy WM_NAME (ASCII) and _NET_WM_NAME (UTF-8), so non-Latin track names survive.</summary>
    public static void SetTitle(IntPtr display, UIntPtr window, string title)
    {
        var asciiTitle = System.Text.Encoding.ASCII.GetBytes(title + "\0");
        fixed (byte* asciiPointer = asciiTitle)
        {
            XStoreName(display, window, asciiPointer);
        }
        var utf8Title = System.Text.Encoding.UTF8.GetBytes(title);
        fixed (byte* utf8Pointer = utf8Title)
        {
            XChangeProperty(display, window, InternAtom(display, "_NET_WM_NAME"), InternAtom(display, "UTF8_STRING"), 8,
                PropertyModeReplace, utf8Pointer, utf8Title.Length);
        }
    }

    /// <summary>Pins the window to one size (fixed-size editors), or clears the pin.</summary>
    public static void SetFixedSize(IntPtr display, UIntPtr window, int width, int height, bool fixedSize)
    {
        var sizeHints = new SizeHints
        {
            Flags = fixedSize ? SizeHintMinimum | SizeHintMaximum : 0,
            MinimumWidth = width,
            MinimumHeight = height,
            MaximumWidth = width,
            MaximumHeight = height,
        };
        XSetWMNormalHints(display, window, &sizeHints);
    }

    /// <summary>A non-blocking eventfd the plugin host thread's poll() wakes on when work is queued.</summary>
    public static int CreateWakeDescriptor() => CreateEventDescriptor(0, EventFdNonBlocking | EventFdCloseOnExec);

    public static void SignalWakeDescriptor(int wakeDescriptor)
    {
        ulong increment = 1;
        WriteDescriptor(wakeDescriptor, &increment, sizeof(ulong));
    }

    public static void DrainWakeDescriptor(int wakeDescriptor)
    {
        ulong counter;
        ReadDescriptor(wakeDescriptor, &counter, sizeof(ulong));
    }
}
