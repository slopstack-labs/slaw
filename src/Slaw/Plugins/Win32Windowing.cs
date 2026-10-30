using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// The slice of user32 a plugin editor needs: a window class, a window, and a
/// message pump. Avalonia owns slaw's own windows; these belong to the plugin
/// host thread.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class Win32Windowing
{
    public const uint WindowMessageDestroy = 0x0002;
    public const uint WindowMessageSize = 0x0005;
    public const uint WindowMessageSetFocus = 0x0007;
    public const uint WindowMessageClose = 0x0010;
    public const uint WindowMessageSizing = 0x0214;
    public const uint WindowMessageDpiChanged = 0x02E0;

    public const uint StyleOverlapped = 0x00000000;
    public const uint StyleCaption = 0x00C00000;
    public const uint StyleSystemMenu = 0x00080000;
    public const uint StyleThickFrame = 0x00040000;
    public const uint StyleMinimizeBox = 0x00020000;
    public const uint StyleClipChildren = 0x02000000;

    public const int UseDefaultPosition = unchecked((int)0x80000000);
    public const int ShowNormal = 1;
    public const int ShowRestore = 9;
    public const int SizeMinimized = 1;
    public const uint PositionNoMove = 0x0002;
    public const uint PositionNoZOrder = 0x0004;
    public const uint PositionNoActivate = 0x0010;
    public const uint QueueStatusAllInput = 0x04FF;
    public const uint WaitInputAvailable = 0x0004;
    private const uint PeekRemove = 0x0001;
    public const uint ChildWindow = 5;
    private const int ArrowCursor = 32512;
    private const int BlackBrush = 4;

    // WM_SIZING edges that move the left or top of the window.
    public static bool SizingMovesLeftEdge(int edge) => edge is 1 or 4 or 7;
    public static bool SizingMovesTopEdge(int edge) => edge is 3 or 4 or 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr WindowHandle;
        public uint MessageId;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public IntPtr MenuName;
        public IntPtr ClassName;
        public IntPtr SmallIcon;
    }

    /// <summary>Dispatches every queued message for the calling thread's windows.</summary>
    public static void DispatchPendingMessages()
    {
        Message message;
        while (PeekMessageW(&message, IntPtr.Zero, 0, 0, PeekRemove) != 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    /// <summary>Registers a window class whose name lives as long as the process. Returns the class name pointer.</summary>
    public static IntPtr RegisterWindowClass(string className, delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr> windowProcedure)
    {
        var classNamePointer = Marshal.StringToHGlobalUni(className);
        var windowClass = new WindowClassEx
        {
            Size = (uint)sizeof(WindowClassEx),
            WindowProcedure = (IntPtr)windowProcedure,
            Instance = GetModuleHandleW(IntPtr.Zero),
            Cursor = LoadCursorW(IntPtr.Zero, (IntPtr)ArrowCursor),
            Background = GetStockObject(BlackBrush),
            ClassName = classNamePointer,
        };
        if (RegisterClassExW(&windowClass) == 0)
        {
            Marshal.FreeHGlobal(classNamePointer);
            throw new InvalidOperationException($"could not register the plugin editor window class (error {Marshal.GetLastWin32Error()})");
        }
        return classNamePointer;
    }

    public static IntPtr CreateWindow(IntPtr className, string title, uint style, int width, int height, IntPtr ownerWindow)
    {
        fixed (char* titlePointer = title)
        {
            return CreateWindowExW(0, className, titlePointer, style, UseDefaultPosition, UseDefaultPosition, width, height,
                ownerWindow, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        }
    }

    /// <summary>The outer window size whose client area is <paramref name="clientWidth"/> × <paramref name="clientHeight"/>.</summary>
    public static (int Width, int Height) WindowSizeForClient(IntPtr windowHandle, uint style, int clientWidth, int clientHeight)
    {
        var bounds = new Rect { Right = clientWidth, Bottom = clientHeight };
        AdjustWindowRectExForDpi(&bounds, style, 0, 0, Math.Max(96u, GetDpiForWindow(windowHandle)));
        return (bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    [DllImport("ole32.dll")]
    public static extern int OleInitialize(IntPtr reserved);

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjectsEx(uint handleCount, IntPtr* handles, uint timeoutMilliseconds, uint wakeMask, uint flags);

    [DllImport("user32.dll")]
    private static extern int PeekMessageW(Message* message, IntPtr windowHandle, uint filterMinimum, uint filterMaximum, uint removeMessage);

    [DllImport("user32.dll")]
    private static extern int TranslateMessage(Message* message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(Message* message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(WindowClassEx* windowClass);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint extendedStyle, IntPtr className, char* windowName, uint style,
        int x, int y, int width, int height, IntPtr parentWindow, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern int IsWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern int ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    public static extern int IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern int SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern int SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern int GetClientRect(IntPtr windowHandle, Rect* clientRect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern int AdjustWindowRectExForDpi(Rect* bounds, uint style, int hasMenu, uint extendedStyle, uint dpi);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr windowHandle, uint relationship);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int stockObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(IntPtr moduleName);
}
