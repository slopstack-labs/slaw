using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// A plugin's own editor (IPlugView) embedded in a top-level X11 window, on
/// Linux (X11 or XWayland). The window marks the studio as its owner
/// (WM_TRANSIENT_FOR), so window managers keep it above the studio. All
/// editors share one Xlib connection, opened by the first and closed by the
/// last, and every member runs on the plugin host thread, whose poll() loop
/// services the connection and the editors' own run-loop registrations.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class Vst3LinuxEditorWindow
{
    private const int FallbackWidth = 800;
    private const int FallbackHeight = 600;

    private static readonly Dictionary<UIntPtr, Vst3LinuxEditorWindow> OpenWindows = new();
    private static IntPtr display;
    private static UIntPtr deleteWindowAtom;
    private static bool threadsInitialized;

    private readonly Action closed;
    private readonly bool resizable;
    private IntPtr plugView;
    private PlugFrame? plugFrame;
    private UIntPtr windowId;
    private int lastWidth;
    private int lastHeight;
    private bool resizingForPlugin;

    private Vst3LinuxEditorWindow(IntPtr plugView, bool resizable, Action closed)
    {
        this.plugView = plugView;
        this.resizable = resizable;
        this.closed = closed;
    }

    /// <summary>The X11 window the editor lives in (for tests and diagnostics).</summary>
    public UIntPtr WindowId => windowId;

    /// <summary>Why editors cannot open here, or null if they can.</summary>
    public static string? UnavailabilityReason() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            ? "plugin editors need an X11 display (or XWayland) and DISPLAY is not set — the knobs in the device view still work"
            : null;

    /// <summary>The shared connection's descriptor for poll(), or -1 while no editor is open.</summary>
    public static int ConnectionDescriptor => display == IntPtr.Zero ? -1 : X11Windowing.XConnectionNumber(display);

    /// <summary>
    /// Creates the controller's editor view and shows it in a new window.
    /// <paramref name="closed"/> runs once, however the window goes away.
    /// Throws <see cref="InvalidOperationException"/> with a readable reason.
    /// </summary>
    public static Vst3LinuxEditorWindow Open(IntPtr editController, string pluginName, string title, IntPtr ownerWindow, Action closed)
    {
        if (UnavailabilityReason() is { } unavailable)
        {
            throw new InvalidOperationException(unavailable);
        }
        IntPtr createdView;
        fixed (byte* editorViewType = Vst3Abi.EditorViewType)
        {
            createdView = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)Vst3Abi.Slot(editController, 17))(editController, editorViewType);
        }
        if (createdView == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{pluginName} has no editor of its own — its knobs are in the device view");
        }
        fixed (byte* x11PlatformType = Vst3Abi.PlatformTypeX11EmbedWindowId)
        {
            if (((delegate* unmanaged<IntPtr, byte*, int>)Vst3Abi.Slot(createdView, 3))(createdView, x11PlatformType) != Vst3Abi.ResultOk)
            {
                Vst3Abi.Release(createdView);
                throw new InvalidOperationException($"{pluginName}'s editor does not support X11 windows on this build — its knobs are in the device view");
            }
        }

        var canResize = ((delegate* unmanaged<IntPtr, int>)Vst3Abi.Slot(createdView, 13))(createdView) == Vst3Abi.ResultOk;
        var editorWindow = new Vst3LinuxEditorWindow(createdView, canResize, closed);
        try
        {
            editorWindow.CreateAndAttach(title, ownerWindow, pluginName);
        }
        catch
        {
            editorWindow.DetachView(reportClosure: false);
            editorWindow.DestroyNativeWindow();
            throw;
        }
        return editorWindow;
    }

    private void CreateAndAttach(string title, IntPtr ownerWindow, string pluginName)
    {
        EnsureDisplay(pluginName);
        var initialSize = ViewSize() ?? new Vst3ViewRect { Right = FallbackWidth, Bottom = FallbackHeight };
        var screenNumber = X11Windowing.XDefaultScreen(display);
        windowId = X11Windowing.XCreateSimpleWindow(display, X11Windowing.XRootWindow(display, screenNumber), 0, 0,
            (uint)initialSize.Width, (uint)initialSize.Height, 0, X11Windowing.XBlackPixel(display, screenNumber), X11Windowing.XBlackPixel(display, screenNumber));
        if (windowId == UIntPtr.Zero)
        {
            throw new InvalidOperationException($"could not create a window for {pluginName}");
        }
        OpenWindows[windowId] = this;
        (lastWidth, lastHeight) = (initialSize.Width, initialSize.Height);
        X11Windowing.XSelectInput(display, windowId, X11Windowing.StructureNotifyMask);
        var deleteAtom = deleteWindowAtom;
        X11Windowing.XSetWMProtocols(display, windowId, &deleteAtom, 1);
        X11Windowing.SetTitle(display, windowId, title);
        X11Windowing.SetFixedSize(display, windowId, initialSize.Width, initialSize.Height, fixedSize: !resizable);
        if (ownerWindow != IntPtr.Zero)
        {
            X11Windowing.XSetTransientForHint(display, windowId, (UIntPtr)(ulong)ownerWindow);
        }
        // The plugin talks to the X server over its own connection: the window must exist there before it is named.
        X11Windowing.XSync(display, 0);

        plugFrame = new PlugFrame(this);
        ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(plugView, 12))(plugView, plugFrame.NativePointer);

        int attachResult;
        fixed (byte* x11PlatformType = Vst3Abi.PlatformTypeX11EmbedWindowId)
        {
            attachResult = ((delegate* unmanaged<IntPtr, IntPtr, byte*, int>)Vst3Abi.Slot(plugView, 4))(plugView, (IntPtr)(ulong)windowId, x11PlatformType);
        }
        if (attachResult != Vst3Abi.ResultOk)
        {
            throw new InvalidOperationException($"{pluginName}'s editor refused to attach to its window (result {attachResult})");
        }

        // Many editors only know their real size once attached.
        if (ViewSize() is { } attachedSize && (attachedSize.Width != lastWidth || attachedSize.Height != lastHeight))
        {
            ResizeWindow(attachedSize.Width, attachedSize.Height);
        }
        X11Windowing.XMapRaised(display, windowId);
        X11Windowing.XFlush(display);
    }

    private static void EnsureDisplay(string pluginName)
    {
        if (display != IntPtr.Zero)
        {
            return;
        }
        if (!threadsInitialized)
        {
            // The studio's own toolkit talks to X from other threads; this connection must be thread-aware too.
            X11Windowing.XInitThreads();
            threadsInitialized = true;
        }
        display = X11Windowing.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            throw new InvalidOperationException($"could not open the X display \"{Environment.GetEnvironmentVariable("DISPLAY")}\" for {pluginName}'s editor");
        }
        deleteWindowAtom = X11Windowing.InternAtom(display, "WM_DELETE_WINDOW");
        PluginHostThread.Wake();
    }

    public void BringToFront()
    {
        if (display != IntPtr.Zero && windowId != UIntPtr.Zero)
        {
            X11Windowing.XMapRaised(display, windowId);
            X11Windowing.XFlush(display);
        }
    }

    /// <summary>Detaches the editor and destroys its window.</summary>
    public void Close()
    {
        DetachView();
        DestroyNativeWindow();
    }

    /// <summary>Handles everything queued on the shared connection. Plugin host thread, from its poll() loop.</summary>
    public static void DispatchPendingEvents()
    {
        if (display == IntPtr.Zero)
        {
            return;
        }
        var eventBuffer = stackalloc byte[X11Windowing.EventSize];
        while (display != IntPtr.Zero && X11Windowing.XPending(display) > 0)
        {
            X11Windowing.XNextEvent(display, eventBuffer);
            var eventType = *(int*)eventBuffer;
            var eventWindow = eventType == X11Windowing.ConfigureNotify || eventType == X11Windowing.DestroyNotify
                ? *(UIntPtr*)(eventBuffer + X11Windowing.ConfigureWindowOffset)
                : *(UIntPtr*)(eventBuffer + X11Windowing.EventWindowOffset);
            if (!OpenWindows.TryGetValue(eventWindow, out var editorWindow))
            {
                continue;
            }
            try
            {
                editorWindow.HandleEvent(eventType, eventBuffer);
            }
            catch (Exception windowRemediationOpportunity)
            {
                // An exception must not take the host loop down; the editor is closed instead of left half-alive.
                Console.Error.WriteLine($"[plugin editor] {windowRemediationOpportunity.Message}");
                editorWindow.Close();
            }
        }
    }

    private void HandleEvent(int eventType, byte* eventBuffer)
    {
        switch (eventType)
        {
            case X11Windowing.ClientMessage when *(UIntPtr*)(eventBuffer + X11Windowing.ClientMessageFirstLongOffset) == deleteWindowAtom:
                Close();
                break;
            case X11Windowing.DestroyNotify:
                // Destroyed from outside: the plugin must still hear removed().
                OpenWindows.Remove(windowId);
                windowId = UIntPtr.Zero;
                DetachView();
                CloseDisplayIfUnused();
                break;
            case X11Windowing.ConfigureNotify when plugView != IntPtr.Zero && !resizingForPlugin:
                var configuredWidth = *(int*)(eventBuffer + X11Windowing.ConfigureWidthOffset);
                var configuredHeight = *(int*)(eventBuffer + X11Windowing.ConfigureHeightOffset);
                if (resizable && (configuredWidth != lastWidth || configuredHeight != lastHeight))
                {
                    ApplyUserResize(configuredWidth, configuredHeight);
                }
                break;
        }
    }

    /// <summary>The user dragged the window: the editor may snap the size, then hears it with onSize.</summary>
    private void ApplyUserResize(int requestedWidth, int requestedHeight)
    {
        var proposedSize = new Vst3ViewRect { Right = requestedWidth, Bottom = requestedHeight };
        ((delegate* unmanaged<IntPtr, Vst3ViewRect*, int>)Vst3Abi.Slot(plugView, 14))(plugView, &proposedSize);
        var constrainedWidth = Math.Max(1, proposedSize.Width);
        var constrainedHeight = Math.Max(1, proposedSize.Height);
        if (constrainedWidth != requestedWidth || constrainedHeight != requestedHeight)
        {
            X11Windowing.XResizeWindow(display, windowId, (uint)constrainedWidth, (uint)constrainedHeight);
            X11Windowing.XFlush(display);
        }
        (lastWidth, lastHeight) = (constrainedWidth, constrainedHeight);
        var viewSize = new Vst3ViewRect { Right = constrainedWidth, Bottom = constrainedHeight };
        ((delegate* unmanaged<IntPtr, Vst3ViewRect*, int>)Vst3Abi.Slot(plugView, 10))(plugView, &viewSize);
    }

    private void ResizeWindow(int width, int height)
    {
        (lastWidth, lastHeight) = (width, height);
        X11Windowing.SetFixedSize(display, windowId, width, height, fixedSize: !resizable);
        X11Windowing.XResizeWindow(display, windowId, (uint)width, (uint)height);
        X11Windowing.XFlush(display);
    }

    private Vst3ViewRect? ViewSize()
    {
        Vst3ViewRect viewSize;
        return ((delegate* unmanaged<IntPtr, Vst3ViewRect*, int>)Vst3Abi.Slot(plugView, 9))(plugView, &viewSize) == Vst3Abi.ResultOk &&
               viewSize.Width > 0 && viewSize.Height > 0
            ? viewSize
            : null;
    }

    /// <summary>IPlugFrame::resizeView: the editor asks for a new size; the host resizes, then confirms with onSize.</summary>
    private int ResizeForPlugin(Vst3ViewRect* requestedSize)
    {
        if (requestedSize is null || plugView == IntPtr.Zero || windowId == UIntPtr.Zero || requestedSize->Width <= 0 || requestedSize->Height <= 0)
        {
            return Vst3Abi.InvalidArgument;
        }
        var alreadyResizing = resizingForPlugin;
        resizingForPlugin = true;
        try
        {
            ResizeWindow(requestedSize->Width, requestedSize->Height);
            if (!alreadyResizing)
            {
                var confirmedSize = new Vst3ViewRect { Right = requestedSize->Width, Bottom = requestedSize->Height };
                ((delegate* unmanaged<IntPtr, Vst3ViewRect*, int>)Vst3Abi.Slot(plugView, 10))(plugView, &confirmedSize);
            }
        }
        finally
        {
            resizingForPlugin = alreadyResizing;
        }
        return Vst3Abi.ResultOk;
    }

    /// <summary>removed(), unhook the frame, release the view, report closure. Safe to call twice.</summary>
    private void DetachView(bool reportClosure = true)
    {
        if (plugView == IntPtr.Zero)
        {
            return;
        }
        var detachedView = plugView;
        plugView = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, int>)Vst3Abi.Slot(detachedView, 5))(detachedView);
        ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(detachedView, 12))(detachedView, IntPtr.Zero);
        Vst3Abi.Release(detachedView);
        plugFrame?.Release();
        plugFrame = null;
        if (reportClosure)
        {
            closed();
        }
    }

    private void DestroyNativeWindow()
    {
        if (windowId != UIntPtr.Zero && display != IntPtr.Zero)
        {
            var destroyedWindow = windowId;
            windowId = UIntPtr.Zero;
            OpenWindows.Remove(destroyedWindow);
            X11Windowing.XDestroyWindow(display, destroyedWindow);
            X11Windowing.XFlush(display);
        }
        CloseDisplayIfUnused();
    }

    private static void CloseDisplayIfUnused()
    {
        if (OpenWindows.Count > 0 || display == IntPtr.Zero)
        {
            return;
        }
        // Drain what the server still says about windows that are gone, then hang up.
        X11Windowing.XSync(display, 1);
        X11Windowing.XCloseDisplay(display);
        display = IntPtr.Zero;
    }

    /// <summary>IPlugFrame: the editor's handle on its window, and how it finds the host's run loop.</summary>
    private sealed class PlugFrame : Vst3HostObject
    {
        private static readonly IntPtr* SharedVtable = BuildVtable(
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, Vst3ViewRect*, int>)&ResizeViewThunk);

        private readonly Vst3LinuxEditorWindow editorWindow;

        public PlugFrame(Vst3LinuxEditorWindow editorWindow)
            : base(SharedVtable)
        {
            this.editorWindow = editorWindow;
        }

        protected override byte[][] InterfaceIds { get; } = { Vst3Abi.PlugFrameIid };

        /// <summary>Linux editors query their frame for Steinberg::Linux::IRunLoop.</summary>
        protected override IntPtr DelegatedInterface(byte* interfaceId) =>
            Vst3Abi.UidEquals(interfaceId, Vst3Abi.RunLoopIid) ? Vst3LinuxRunLoop.Shared.AcquireReference() : IntPtr.Zero;

        [UnmanagedCallersOnly]
        private static int ResizeViewThunk(IntPtr self, IntPtr view, Vst3ViewRect* newSize)
        {
            try
            {
                return Resolve<PlugFrame>(self)?.editorWindow.ResizeForPlugin(newSize) ?? Vst3Abi.InvalidArgument;
            }
            catch (Exception resizeRemediationOpportunity)
            {
                Console.Error.WriteLine($"[plugin editor] resize failed: {resizeRemediationOpportunity.Message}");
                return Vst3Abi.InvalidArgument;
            }
        }
    }
}
