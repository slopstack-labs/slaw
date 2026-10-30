using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// Steinberg::Linux::IRunLoop: Linux has no global event loop, so a host lends
/// its own to plugin editors. Editors register file descriptors (their X11
/// connections, mostly) and timers (their repaints and animations); the plugin
/// host thread's poll() loop calls them back. One process-wide run loop serves
/// every editor, and every callback happens on the plugin host thread, the same
/// thread that creates and talks to the plugins' controllers and views.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed unsafe class Vst3LinuxRunLoop : Vst3HostObject
{
    private static readonly IntPtr* SharedVtable = BuildVtable(
        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int, int>)&RegisterEventHandlerThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&UnregisterEventHandlerThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, ulong, int>)&RegisterTimerThunk,
        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&UnregisterTimerThunk);

    /// <summary>Plugins may keep it forever, so it lives forever.</summary>
    public static readonly Vst3LinuxRunLoop Shared = new();

    private static readonly object RegistryLock = new();
    private static readonly List<EventRegistration> EventHandlers = new();
    private static readonly List<TimerRegistration> Timers = new();

    private sealed record EventRegistration(IntPtr Handler, int FileDescriptor);

    private sealed class TimerRegistration
    {
        public required IntPtr Handler { get; init; }
        public required long IntervalMilliseconds { get; init; }
        public long DueMilliseconds { get; set; }
        public bool Unregistered { get; set; }
    }

    private Vst3LinuxRunLoop()
        : base(SharedVtable)
    {
    }

    protected override byte[][] InterfaceIds { get; } = { Vst3Abi.RunLoopIid };

    private static long NowMilliseconds => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    public static int RegisteredHandlerCount
    {
        get
        {
            lock (RegistryLock)
            {
                return EventHandlers.Count + Timers.Count;
            }
        }
    }

    // ── The plugin host thread's side ─────────────────────────────────────────

    /// <summary>Milliseconds until the nearest timer is due (0 if one is overdue), or null without timers.</summary>
    public static long? MillisecondsUntilNextTimer()
    {
        lock (RegistryLock)
        {
            if (Timers.Count == 0)
            {
                return null;
            }
            var now = NowMilliseconds;
            return Math.Max(0, Timers.Min(timer => timer.DueMilliseconds) - now);
        }
    }

    /// <summary>The descriptors editors asked to be woken for, for poll().</summary>
    public static void CollectFileDescriptors(List<int> fileDescriptors)
    {
        lock (RegistryLock)
        {
            foreach (var registration in EventHandlers)
            {
                if (!fileDescriptors.Contains(registration.FileDescriptor))
                {
                    fileDescriptors.Add(registration.FileDescriptor);
                }
            }
        }
    }

    /// <summary>Calls onFDIsSet on every handler watching <paramref name="readyDescriptor"/>.</summary>
    public static void DispatchReadyDescriptor(int readyDescriptor)
    {
        List<IntPtr> readyHandlers;
        lock (RegistryLock)
        {
            readyHandlers = EventHandlers.Where(registration => registration.FileDescriptor == readyDescriptor).Select(registration => registration.Handler).ToList();
            readyHandlers.ForEach(AddReference);
        }
        foreach (var readyHandler in readyHandlers)
        {
            // Called without the lock: handlers may (un)register from inside their callback.
            ((delegate* unmanaged<IntPtr, int, void>)Vst3Abi.Slot(readyHandler, 3))(readyHandler, readyDescriptor);
            Vst3Abi.Release(readyHandler);
        }
    }

    /// <summary>Calls onTimer on every timer that is due, then schedules its next tick.</summary>
    public static void FireDueTimers()
    {
        List<TimerRegistration> dueTimers;
        var now = NowMilliseconds;
        lock (RegistryLock)
        {
            dueTimers = Timers.Where(timer => timer.DueMilliseconds <= now).ToList();
            foreach (var dueTimer in dueTimers)
            {
                AddReference(dueTimer.Handler);
                // Late ticks are not replayed: an editor that missed three repaints wants one, not three.
                dueTimer.DueMilliseconds = now + dueTimer.IntervalMilliseconds;
            }
        }
        foreach (var dueTimer in dueTimers)
        {
            if (!dueTimer.Unregistered)
            {
                ((delegate* unmanaged<IntPtr, void>)Vst3Abi.Slot(dueTimer.Handler, 3))(dueTimer.Handler);
            }
            Vst3Abi.Release(dueTimer.Handler);
        }
    }

    private static void AddReference(IntPtr comObject) => ((delegate* unmanaged<IntPtr, uint>)Vst3Abi.Slot(comObject, 1))(comObject);

    // ── IRunLoop (called by plugins, normally on the plugin host thread) ──────

    [UnmanagedCallersOnly]
    private static int RegisterEventHandlerThunk(IntPtr self, IntPtr eventHandler, int fileDescriptor)
    {
        if (eventHandler == IntPtr.Zero || fileDescriptor < 0)
        {
            return Vst3Abi.InvalidArgument;
        }
        AddReference(eventHandler);
        lock (RegistryLock)
        {
            EventHandlers.Add(new EventRegistration(eventHandler, fileDescriptor));
        }
        PluginHostThread.Wake();
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int UnregisterEventHandlerThunk(IntPtr self, IntPtr eventHandler)
    {
        List<EventRegistration> removedRegistrations;
        lock (RegistryLock)
        {
            removedRegistrations = EventHandlers.Where(registration => registration.Handler == eventHandler).ToList();
            EventHandlers.RemoveAll(registration => registration.Handler == eventHandler);
        }
        removedRegistrations.ForEach(registration => Vst3Abi.Release(registration.Handler));
        return removedRegistrations.Count > 0 ? Vst3Abi.ResultOk : Vst3Abi.InvalidArgument;
    }

    [UnmanagedCallersOnly]
    private static int RegisterTimerThunk(IntPtr self, IntPtr timerHandler, ulong intervalMilliseconds)
    {
        if (timerHandler == IntPtr.Zero)
        {
            return Vst3Abi.InvalidArgument;
        }
        AddReference(timerHandler);
        var clampedInterval = (long)Math.Clamp(intervalMilliseconds, 1UL, 60_000UL);
        lock (RegistryLock)
        {
            Timers.Add(new TimerRegistration
            {
                Handler = timerHandler,
                IntervalMilliseconds = clampedInterval,
                DueMilliseconds = NowMilliseconds + clampedInterval,
            });
        }
        PluginHostThread.Wake();
        return Vst3Abi.ResultOk;
    }

    [UnmanagedCallersOnly]
    private static int UnregisterTimerThunk(IntPtr self, IntPtr timerHandler)
    {
        List<TimerRegistration> removedTimers;
        lock (RegistryLock)
        {
            removedTimers = Timers.Where(timer => timer.Handler == timerHandler).ToList();
            Timers.RemoveAll(timer => timer.Handler == timerHandler);
        }
        foreach (var removedTimer in removedTimers)
        {
            removedTimer.Unregistered = true;
            Vst3Abi.Release(removedTimer.Handler);
        }
        return removedTimers.Count > 0 ? Vst3Abi.ResultOk : Vst3Abi.InvalidArgument;
    }
}
