using System.Runtime.InteropServices;
using SlopstackLabs.Slaw.Plugins;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>
/// The run loop slaw lends Linux plugin editors (Steinberg::Linux::IRunLoop),
/// driven the way an editor drives it: through its vtable, with handler objects
/// of its own. No X server needed; the real editors are covered by the opt-in
/// test in <see cref="Vst3EditorTests"/> (SLAW_TEST_PLUGIN_EDITORS) on Linux too.
/// </summary>
public class Vst3LinuxRunLoopTests
{
    private const int RegisterEventHandlerSlot = 3;
    private const int UnregisterEventHandlerSlot = 4;
    private const int RegisterTimerSlot = 5;
    private const int UnregisterTimerSlot = 6;

    /// <summary>A plugin-side ITimerHandler / IEventHandler: counts calls and notes the thread.</summary>
    private sealed unsafe class CountingHandler : Vst3HostObject
    {
        private static readonly IntPtr* SharedVtable = BuildVtable((IntPtr)(delegate* unmanaged<IntPtr, int, void>)&OnCallbackThunk);

        public CountingHandler(int drainedDescriptor = -1)
            : base(SharedVtable)
        {
            DrainedDescriptor = drainedDescriptor;
        }

        public int DrainedDescriptor { get; }
        public int Calls;
        public int LastDescriptor = -1;
        public bool AllOnHostThread = true;

        protected override byte[][] InterfaceIds { get; } = { Vst3Abi.TimerHandlerIid, Vst3Abi.EventHandlerIid };

        // onTimer() takes only `this`; onFDIsSet(fd) also takes the descriptor. Reading the second argument
        // for onTimer reads an unused register, which the counting below ignores.
        [UnmanagedCallersOnly]
        private static void OnCallbackThunk(IntPtr self, int fileDescriptor)
        {
            if (Resolve<CountingHandler>(self) is not { } handler)
            {
                return;
            }
            Interlocked.Increment(ref handler.Calls);
            handler.AllOnHostThread &= PluginHostThread.IsCurrent;
            if (handler.DrainedDescriptor >= 0 && OperatingSystem.IsLinux())
            {
                handler.LastDescriptor = fileDescriptor;
                X11Windowing.DrainWakeDescriptor(handler.DrainedDescriptor);
            }
        }
    }

    private static unsafe int CallRunLoop(int slot, IntPtr handler, ulong argument)
    {
        if (!OperatingSystem.IsLinux())
        {
            return -1;
        }
        var runLoop = Vst3LinuxRunLoop.Shared.NativePointer;
        return slot switch
        {
            RegisterEventHandlerSlot => ((delegate* unmanaged<IntPtr, IntPtr, int, int>)Vst3Abi.Slot(runLoop, slot))(runLoop, handler, (int)argument),
            RegisterTimerSlot => ((delegate* unmanaged<IntPtr, IntPtr, ulong, int>)Vst3Abi.Slot(runLoop, slot))(runLoop, handler, argument),
            _ => ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(runLoop, slot))(runLoop, handler),
        };
    }

    [Fact]
    public void TimersTickOnThePluginHostThreadUntilUnregistered()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var timer = new CountingHandler();
        Assert.Equal(Vst3Abi.ResultOk, CallRunLoop(RegisterTimerSlot, timer.NativePointer, 10));
        Thread.Sleep(300);
        Assert.Equal(Vst3Abi.ResultOk, CallRunLoop(UnregisterTimerSlot, timer.NativePointer, 0));
        var callsAtUnregister = Volatile.Read(ref timer.Calls);
        Thread.Sleep(150);

        Assert.InRange(callsAtUnregister, 5, 40);
        Assert.True(timer.AllOnHostThread, "editors' timers must fire on the plugin host thread");
        Assert.Equal(callsAtUnregister, Volatile.Read(ref timer.Calls));
        Assert.Equal(Vst3Abi.InvalidArgument, CallRunLoop(UnregisterTimerSlot, timer.NativePointer, 0));
        timer.Release();
    }

    [Fact]
    public void DescriptorsWakeTheirHandlerOnThePluginHostThread()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var watchedDescriptor = X11Windowing.CreateWakeDescriptor();
        Assert.True(watchedDescriptor >= 0);
        var eventHandler = new CountingHandler(watchedDescriptor);
        Assert.Equal(Vst3Abi.ResultOk, CallRunLoop(RegisterEventHandlerSlot, eventHandler.NativePointer, (ulong)watchedDescriptor));
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref eventHandler.Calls));

        X11Windowing.SignalWakeDescriptor(watchedDescriptor);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref eventHandler.Calls) == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.Equal(Vst3Abi.ResultOk, CallRunLoop(UnregisterEventHandlerSlot, eventHandler.NativePointer, 0));
        Assert.True(Volatile.Read(ref eventHandler.Calls) >= 1, "a readable descriptor should call onFDIsSet");
        Assert.Equal(watchedDescriptor, eventHandler.LastDescriptor);
        Assert.True(eventHandler.AllOnHostThread);
        eventHandler.Release();
    }

    [Fact]
    public unsafe void TheRunLoopAnswersToItsInterfaceId()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var runLoop = Vst3LinuxRunLoop.Shared.NativePointer;
        IntPtr queried;
        fixed (byte* runLoopIid = Vst3Abi.RunLoopIid)
        {
            Assert.Equal(Vst3Abi.ResultOk, ((delegate* unmanaged<IntPtr, byte*, IntPtr*, int>)Vst3Abi.Slot(runLoop, 0))(runLoop, runLoopIid, &queried));
        }
        Assert.Equal(runLoop, queried);
        Vst3Abi.Release(queried);
    }
}
