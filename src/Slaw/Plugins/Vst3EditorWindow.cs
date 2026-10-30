using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// A plugin's own editor (IPlugView) in a top-level Win32 window. The window is
/// owned by the studio window, so it floats above it and minimizes with it, and
/// it lives on the plugin host thread, whose message loop pumps it. Every member
/// is plugin host thread only.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class Vst3EditorWindow
{
    private const uint FixedSizeStyle = Win32Windowing.StyleOverlapped | Win32Windowing.StyleCaption | Win32Windowing.StyleSystemMenu |
                                        Win32Windowing.StyleMinimizeBox | Win32Windowing.StyleClipChildren;
    private const uint ResizableStyle = FixedSizeStyle | Win32Windowing.StyleThickFrame;
    private const int FallbackWidth = 800;
    private const int FallbackHeight = 600;

    private static readonly Dictionary<IntPtr, Vst3EditorWindow> OpenWindows = new();
    private static IntPtr windowClassName;

    private readonly Action closed;
    private readonly uint windowStyle;
    private readonly bool resizable;
    private IntPtr plugView;
    private PlugFrame? plugFrame;
    private IntPtr windowHandle;
    private bool resizingForPlugin;

    private Vst3EditorWindow(IntPtr plugView, bool resizable, Action closed)
    {
        this.plugView = plugView;
        this.resizable = resizable;
        this.closed = closed;
        windowStyle = resizable ? ResizableStyle : FixedSizeStyle;
    }

    /// <summary>
    /// Creates the controller's editor view and shows it in a new window.
    /// <paramref name="closed"/> runs once, however the window goes away.
    /// Throws <see cref="InvalidOperationException"/> with a readable reason.
    /// </summary>
    public static Vst3EditorWindow Open(IntPtr editController, string pluginName, string title, IntPtr ownerWindow, Action closed)
    {
        IntPtr createdView;
        fixed (byte* editorViewType = Vst3Abi.EditorViewType)
        {
            createdView = ((delegate* unmanaged<IntPtr, byte*, IntPtr>)Vst3Abi.Slot(editController, 17))(editController, editorViewType);
        }
        if (createdView == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{pluginName} has no editor of its own — its knobs are in the device view");
        }
        fixed (byte* hwndPlatformType = Vst3Abi.PlatformTypeHwnd)
        {
            if (((delegate* unmanaged<IntPtr, byte*, int>)Vst3Abi.Slot(createdView, 3))(createdView, hwndPlatformType) != Vst3Abi.ResultOk)
            {
                Vst3Abi.Release(createdView);
                throw new InvalidOperationException($"{pluginName}'s editor does not support Windows windows (HWND), somehow");
            }
        }

        var canResize = ((delegate* unmanaged<IntPtr, int>)Vst3Abi.Slot(createdView, 13))(createdView) == Vst3Abi.ResultOk;
        var editorWindow = new Vst3EditorWindow(createdView, canResize, closed);
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
        if (windowClassName == IntPtr.Zero)
        {
            windowClassName = Win32Windowing.RegisterWindowClass("slaw.PluginEditor",
                (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WindowProcedure);
        }
        var initialSize = ViewSize() ?? new Vst3ViewRect { Right = FallbackWidth, Bottom = FallbackHeight };
        windowHandle = Win32Windowing.CreateWindow(windowClassName, title, windowStyle, initialSize.Width, initialSize.Height, ownerWindow);
        if (windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"could not create a window for {pluginName} (error {Marshal.GetLastWin32Error()})");
        }
        OpenWindows[windowHandle] = this;
        SizeClientArea(initialSize.Width, initialSize.Height);

        plugFrame = new PlugFrame(this);
        ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(plugView, 12))(plugView, plugFrame.NativePointer);
        ApplyContentScale(Win32Windowing.GetDpiForWindow(windowHandle));

        int attachResult;
        fixed (byte* hwndPlatformType = Vst3Abi.PlatformTypeHwnd)
        {
            attachResult = ((delegate* unmanaged<IntPtr, IntPtr, byte*, int>)Vst3Abi.Slot(plugView, 4))(plugView, windowHandle, hwndPlatformType);
        }
        if (attachResult != Vst3Abi.ResultOk)
        {
            throw new InvalidOperationException($"{pluginName}'s editor refused to attach to its window (result {attachResult})");
        }

        // Many editors only know their real size once attached.
        if (ViewSize() is { } attachedSize)
        {
            SizeClientArea(attachedSize.Width, attachedSize.Height);
        }
        Win32Windowing.ShowWindow(windowHandle, Win32Windowing.ShowNormal);
        Win32Windowing.SetForegroundWindow(windowHandle);
    }

    public void BringToFront()
    {
        if (Win32Windowing.IsIconic(windowHandle) != 0)
        {
            Win32Windowing.ShowWindow(windowHandle, Win32Windowing.ShowRestore);
        }
        Win32Windowing.SetForegroundWindow(windowHandle);
    }

    /// <summary>Detaches the editor and destroys its window.</summary>
    public void Close()
    {
        DetachView();
        DestroyNativeWindow();
    }

    private Vst3ViewRect? ViewSize()
    {
        Vst3ViewRect viewSize;
        return ((delegate* unmanaged<IntPtr, Vst3ViewRect*, int>)Vst3Abi.Slot(plugView, 9))(plugView, &viewSize) == Vst3Abi.ResultOk &&
               viewSize.Width > 0 && viewSize.Height > 0
            ? viewSize
            : null;
    }

    private void SizeClientArea(int clientWidth, int clientHeight)
    {
        var (windowWidth, windowHeight) = Win32Windowing.WindowSizeForClient(windowHandle, windowStyle, clientWidth, clientHeight);
        Win32Windowing.SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, windowWidth, windowHeight,
            Win32Windowing.PositionNoMove | Win32Windowing.PositionNoZOrder | Win32Windowing.PositionNoActivate);
    }

    private void ApplyContentScale(uint dotsPerInch)
    {
        var scaleSupport = Vst3Abi.QueryInterface(plugView, Vst3Abi.PlugViewContentScaleSupportIid);
        if (scaleSupport == IntPtr.Zero)
        {
            return;
        }
        ((delegate* unmanaged<IntPtr, float, int>)Vst3Abi.Slot(scaleSupport, 3))(scaleSupport, Math.Max(96u, dotsPerInch) / 96f);
        Vst3Abi.Release(scaleSupport);
    }

    private void TellViewItsSize()
    {
        Win32Windowing.Rect clientRect;
        Win32Windowing.GetClientRect(windowHandle, &clientRect);
        var viewSize = new Vst3ViewRect { Right = clientRect.Right - clientRect.Left, Bottom = clientRect.Bottom - clientRect.Top };
        if (viewSize.Width > 0 && viewSize.Height > 0)
        {
            ((delegate* unmanaged<IntPtr, Vst3ViewRect*, int>)Vst3Abi.Slot(plugView, 10))(plugView, &viewSize);
        }
    }

    /// <summary>IPlugFrame::resizeView: the editor asks for a new size; the host resizes, then confirms with onSize.</summary>
    private int ResizeForPlugin(Vst3ViewRect* requestedSize)
    {
        if (requestedSize is null || plugView == IntPtr.Zero || requestedSize->Width <= 0 || requestedSize->Height <= 0)
        {
            return Vst3Abi.InvalidArgument;
        }
        var alreadyResizing = resizingForPlugin;
        resizingForPlugin = true;
        try
        {
            SizeClientArea(requestedSize->Width, requestedSize->Height);
        }
        finally
        {
            resizingForPlugin = alreadyResizing;
        }
        if (!alreadyResizing)
        {
            TellViewItsSize();
        }
        return Vst3Abi.ResultOk;
    }

    /// <summary>WM_SIZING: lets a resizable editor veto or snap the size the user is dragging to.</summary>
    private void ConstrainUserResize(int draggedEdge, Win32Windowing.Rect* windowBounds)
    {
        var (frameWidth, frameHeight) = Win32Windowing.WindowSizeForClient(windowHandle, windowStyle, 0, 0);
        var proposedSize = new Vst3ViewRect
        {
            Right = windowBounds->Right - windowBounds->Left - frameWidth,
            Bottom = windowBounds->Bottom - windowBounds->Top - frameHeight,
        };
        ((delegate* unmanaged<IntPtr, Vst3ViewRect*, int>)Vst3Abi.Slot(plugView, 14))(plugView, &proposedSize);
        var constrainedWidth = Math.Max(1, proposedSize.Width) + frameWidth;
        var constrainedHeight = Math.Max(1, proposedSize.Height) + frameHeight;
        if (Win32Windowing.SizingMovesLeftEdge(draggedEdge))
        {
            windowBounds->Left = windowBounds->Right - constrainedWidth;
        }
        else
        {
            windowBounds->Right = windowBounds->Left + constrainedWidth;
        }
        if (Win32Windowing.SizingMovesTopEdge(draggedEdge))
        {
            windowBounds->Top = windowBounds->Bottom - constrainedHeight;
        }
        else
        {
            windowBounds->Bottom = windowBounds->Top + constrainedHeight;
        }
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
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }
        var destroyedWindow = windowHandle;
        windowHandle = IntPtr.Zero;
        OpenWindows.Remove(destroyedWindow);
        if (Win32Windowing.IsWindow(destroyedWindow) != 0)
        {
            Win32Windowing.DestroyWindow(destroyedWindow);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WindowProcedure(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (OpenWindows.TryGetValue(windowHandle, out var editorWindow) && editorWindow.HandleMessage(message, wParam, lParam) is { } handledResult)
            {
                return handledResult;
            }
        }
        catch (Exception windowRemediationOpportunity)
        {
            // An exception must not unwind into user32; the editor is closed instead of left half-alive.
            Console.Error.WriteLine($"[plugin editor] {windowRemediationOpportunity.Message}");
            if (OpenWindows.TryGetValue(windowHandle, out var failedWindow))
            {
                failedWindow.Close();
            }
        }
        return Win32Windowing.DefWindowProcW(windowHandle, message, wParam, lParam);
    }

    /// <summary>Returns a result for messages handled here, or null to let DefWindowProc have them.</summary>
    private IntPtr? HandleMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case Win32Windowing.WindowMessageClose:
                Close();
                return IntPtr.Zero;
            case Win32Windowing.WindowMessageDestroy:
                // Destroyed from outside (the owner went away): the plugin must still hear removed().
                OpenWindows.Remove(windowHandle);
                windowHandle = IntPtr.Zero;
                DetachView();
                return null;
            case Win32Windowing.WindowMessageSizing when resizable && plugView != IntPtr.Zero:
                ConstrainUserResize((int)wParam, (Win32Windowing.Rect*)lParam);
                return 1;
            case Win32Windowing.WindowMessageSize when !resizingForPlugin && plugView != IntPtr.Zero && (int)wParam != Win32Windowing.SizeMinimized:
                TellViewItsSize();
                return IntPtr.Zero;
            case Win32Windowing.WindowMessageDpiChanged when plugView != IntPtr.Zero:
                ApplyContentScale((uint)((long)wParam & 0xFFFF));
                var suggestedBounds = (Win32Windowing.Rect*)lParam;
                Win32Windowing.SetWindowPos(windowHandle, IntPtr.Zero, suggestedBounds->Left, suggestedBounds->Top,
                    suggestedBounds->Right - suggestedBounds->Left, suggestedBounds->Bottom - suggestedBounds->Top,
                    Win32Windowing.PositionNoZOrder | Win32Windowing.PositionNoActivate);
                return IntPtr.Zero;
            case Win32Windowing.WindowMessageSetFocus:
                // Keyboard focus belongs to the plugin's own child window, where its text fields live.
                if (Win32Windowing.GetWindow(windowHandle, Win32Windowing.ChildWindow) is var pluginChild && pluginChild != IntPtr.Zero)
                {
                    Win32Windowing.SetFocus(pluginChild);
                    return IntPtr.Zero;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>IPlugFrame: the editor's handle on its window, used to ask for a new size.</summary>
    private sealed class PlugFrame : Vst3HostObject
    {
        private static readonly IntPtr* SharedVtable = BuildVtable(
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, Vst3ViewRect*, int>)&ResizeViewThunk);

        private readonly Vst3EditorWindow editorWindow;

        public PlugFrame(Vst3EditorWindow editorWindow)
            : base(SharedVtable)
        {
            this.editorWindow = editorWindow;
        }

        protected override byte[][] InterfaceIds { get; } = { Vst3Abi.PlugFrameIid };

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
