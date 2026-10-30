using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SlopstackLabs.Slaw.Plugins;

/// <summary>
/// The one thread allowed to load, initialize, query and tear down plugins.
/// VST3 calls this the "UI thread"; slaw has an Avalonia UI thread already and
/// would rather a misbehaving plugin froze something less visible.
///
/// On Windows it is also a real UI thread: a single-threaded apartment that
/// pumps window messages, because plugin editors are windows, and plugins
/// (JUCE ones especially) run their timers and repaints through the message
/// queue of the thread that created them. Tickers run every
/// <see cref="TickInterval"/> on every platform.
/// </summary>
internal static class PluginHostThread
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(30);

    private static readonly ConcurrentQueue<Action> PendingWork = new();
    private static readonly AutoResetEvent WorkAvailable = new(false);
    private static readonly List<Action> Tickers = new();
    private static readonly Thread HostThread = StartHostThread();

    public static bool IsCurrent => Thread.CurrentThread == HostThread;

    private static Thread StartHostThread()
    {
        var hostThread = new Thread(OperatingSystem.IsWindows() ? RunWindowsMessageLoop : RunWorkLoop)
        {
            IsBackground = true,
            Name = "slaw plugin host",
        };
        if (OperatingSystem.IsWindows())
        {
            hostThread.SetApartmentState(ApartmentState.STA);
        }
        hostThread.Start();
        return hostThread;
    }

    private static void RunWorkLoop()
    {
        while (true)
        {
            WorkAvailable.WaitOne(TickInterval);
            RunPendingWork();
            RunTickers();
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void RunWindowsMessageLoop()
    {
        // Plugin editors take drag and drop and the clipboard, both of which want OLE on their thread.
        Win32Windowing.OleInitialize(IntPtr.Zero);
        var workAvailableHandle = WorkAvailable.SafeWaitHandle.DangerousGetHandle();
        var lastTickMilliseconds = Environment.TickCount64;
        while (true)
        {
            RunPendingWork();
            Win32Windowing.DispatchPendingMessages();
            if (Environment.TickCount64 - lastTickMilliseconds >= (long)TickInterval.TotalMilliseconds)
            {
                lastTickMilliseconds = Environment.TickCount64;
                RunTickers();
            }
            Win32Windowing.MsgWaitForMultipleObjectsEx(1, &workAvailableHandle, (uint)TickInterval.TotalMilliseconds,
                Win32Windowing.QueueStatusAllInput, Win32Windowing.WaitInputAvailable);
        }
    }

    private static void RunPendingWork()
    {
        while (PendingWork.TryDequeue(out var workItem))
        {
            workItem();
        }
    }

    private static void RunTickers()
    {
        for (var tickerIndex = Tickers.Count - 1; tickerIndex >= 0; tickerIndex--)
        {
            try
            {
                Tickers[tickerIndex]();
            }
            catch (Exception tickerRemediationOpportunity)
            {
                // A ticker that throws once will throw again; it is retired rather than allowed to spam.
                Console.Error.WriteLine($"[plugin host] ticker retired: {tickerRemediationOpportunity.Message}");
                Tickers.RemoveAt(tickerIndex);
            }
        }
    }

    /// <summary>Runs <paramref name="ticker"/> every <see cref="TickInterval"/> until removed. Plugin host thread only.</summary>
    public static void AddTicker(Action ticker) => Tickers.Add(ticker);

    /// <summary>Plugin host thread only.</summary>
    public static void RemoveTicker(Action ticker) => Tickers.Remove(ticker);

    public static Task<T> InvokeAsync<T>(Func<T> pluginWork)
    {
        if (IsCurrent)
        {
            try
            {
                return Task.FromResult(pluginWork());
            }
            catch (Exception pluginRemediationOpportunity)
            {
                return Task.FromException<T>(pluginRemediationOpportunity);
            }
        }
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingWork.Enqueue(() =>
        {
            try
            {
                completion.SetResult(pluginWork());
            }
            catch (Exception pluginRemediationOpportunity)
            {
                completion.SetException(pluginRemediationOpportunity);
            }
        });
        WorkAvailable.Set();
        return completion.Task;
    }

    public static Task InvokeAsync(Action pluginWork) => InvokeAsync(() =>
    {
        pluginWork();
        return true;
    });

    public static T Invoke<T>(Func<T> pluginWork) => InvokeAsync(pluginWork).GetAwaiter().GetResult();
}

/// <summary>One class a plugin factory offers.</summary>
internal sealed record Vst3PluginClass(string ClassId, string Name, string Category, string SubCategories, string Vendor, string Version)
{
    public bool IsAudioModule => Category == Vst3Abi.AudioModuleClassCategory;

    /// <summary>Instruments say so in their subcategories ("Instrument|Synth"); everything else is an effect.</summary>
    public bool IsInstrument => SubCategories.Contains("Instrument", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A loaded VST3 binary and its plugin factory. Modules are never unloaded:
/// plugins leave threads and atexit handlers behind, and dlclose is how hosts
/// find out.
/// </summary>
internal sealed unsafe class Vst3Module
{
    private static readonly Dictionary<string, Vst3Module> LoadedModules = new(StringComparer.Ordinal);

    private Vst3Module(string bundlePath, string binaryPath, IntPtr factory, IReadOnlyList<Vst3PluginClass> classes, string factoryVendor)
    {
        BundlePath = bundlePath;
        BinaryPath = binaryPath;
        Factory = factory;
        Classes = classes;
        FactoryVendor = factoryVendor;
    }

    public string BundlePath { get; }
    public string BinaryPath { get; }
    public IntPtr Factory { get; }
    public IReadOnlyList<Vst3PluginClass> Classes { get; }
    public string FactoryVendor { get; }

    /// <summary>
    /// The shared library inside a bundle: Contents/&lt;arch&gt;-linux/&lt;name&gt;.so on Linux,
    /// Contents/&lt;arch&gt;-win/&lt;name&gt;.vst3 on Windows. A plain file is taken as-is (legacy single-file plugins).
    /// </summary>
    public static string? ResolveBinaryPath(string bundlePath)
    {
        if (File.Exists(bundlePath))
        {
            return bundlePath;
        }
        if (!Directory.Exists(bundlePath))
        {
            return null;
        }
        var bundleName = Path.GetFileNameWithoutExtension(bundlePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var architectureName = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => OperatingSystem.IsWindows() ? "arm64" : "aarch64",
            Architecture.X86 => "x86",
            _ => "x86_64",
        };
        var (platformSuffix, binaryExtension) = OperatingSystem.IsWindows() ? ("-win", ".vst3") : ("-linux", ".so");
        var architectureFolder = Path.Combine(bundlePath, "Contents", architectureName + platformSuffix);
        var expectedBinary = Path.Combine(architectureFolder, bundleName + binaryExtension);
        if (File.Exists(expectedBinary))
        {
            return expectedBinary;
        }
        return Directory.Exists(architectureFolder)
            ? Directory.EnumerateFiles(architectureFolder, "*" + binaryExtension).FirstOrDefault()
            : null;
    }

    public const string WindowsPluginExplanation =
        "Windows plugin — it cannot load on Linux. A bridge such as yabridge can wrap it; slaw scans ~/.vst3 (recursively) for the result.";

    /// <summary>True for a PE file ("MZ"), i.e. a Windows DLL wearing a .vst3 extension.</summary>
    public static bool IsWindowsBinary(string binaryPath)
    {
        try
        {
            using var binaryStream = File.OpenRead(binaryPath);
            Span<byte> header = stackalloc byte[2];
            return binaryStream.Read(header) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
        }
        catch (Exception headerRemediationOpportunity) when (headerRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Loads (or recalls) the module for a bundle. Plugin host thread only.</summary>
    public static Vst3Module Load(string bundlePath)
    {
        if (!PluginHostThread.IsCurrent)
        {
            return PluginHostThread.Invoke(() => Load(bundlePath));
        }
        var binaryPath = ResolveBinaryPath(bundlePath)
            ?? throw new FileNotFoundException(
                !OperatingSystem.IsWindows() && Directory.Exists(Path.Combine(bundlePath, "Contents", "x86_64-win"))
                    ? WindowsPluginExplanation
                    : $"no {(OperatingSystem.IsWindows() ? "Windows" : "Linux")} binary inside the VST3 bundle",
                bundlePath);
        if (LoadedModules.TryGetValue(binaryPath, out var loadedModule))
        {
            return loadedModule;
        }
        if (!OperatingSystem.IsWindows() && IsWindowsBinary(binaryPath))
        {
            // dlopen would only fail with "invalid ELF header", which helps nobody.
            throw new NotSupportedException(WindowsPluginExplanation);
        }

        var libraryHandle = NativeLibrary.Load(binaryPath);
        if (OperatingSystem.IsWindows())
        {
            if (NativeLibrary.TryGetExport(libraryHandle, "InitDll", out var initDllExport))
            {
                ((delegate* unmanaged<byte>)initDllExport)();
            }
        }
        else if (NativeLibrary.TryGetExport(libraryHandle, "ModuleEntry", out var moduleEntryExport))
        {
            if (((delegate* unmanaged<IntPtr, byte>)moduleEntryExport)(libraryHandle) == 0)
            {
                throw new InvalidOperationException($"{Path.GetFileName(binaryPath)}: ModuleEntry declined to participate");
            }
        }
        if (!NativeLibrary.TryGetExport(libraryHandle, "GetPluginFactory", out var getPluginFactoryExport))
        {
            throw new InvalidOperationException($"{Path.GetFileName(binaryPath)} has no GetPluginFactory: not a VST3 plugin, or an exceptionally shy one");
        }
        var factory = ((delegate* unmanaged<IntPtr>)getPluginFactoryExport)();
        if (factory == IntPtr.Zero)
        {
            throw new InvalidOperationException($"{Path.GetFileName(binaryPath)}: GetPluginFactory returned nothing");
        }

        var factory3 = Vst3Abi.QueryInterface(factory, Vst3Abi.PluginFactory3Iid);
        if (factory3 != IntPtr.Zero)
        {
            ((delegate* unmanaged<IntPtr, IntPtr, int>)Vst3Abi.Slot(factory3, 9))(factory3, Vst3HostApplication.Shared.NativePointer);
            Vst3Abi.Release(factory3);
        }

        Vst3FactoryInfo factoryInfo;
        ((delegate* unmanaged<IntPtr, Vst3FactoryInfo*, int>)Vst3Abi.Slot(factory, 3))(factory, &factoryInfo);
        var factoryVendor = Vst3Abi.ReadAscii(factoryInfo.Vendor, 64);

        var module = new Vst3Module(bundlePath, binaryPath, factory, EnumerateClasses(factory, factoryVendor), factoryVendor);
        LoadedModules[binaryPath] = module;
        return module;
    }

    private static List<Vst3PluginClass> EnumerateClasses(IntPtr factory, string factoryVendor)
    {
        var factory2 = Vst3Abi.QueryInterface(factory, Vst3Abi.PluginFactory2Iid);
        var classCount = ((delegate* unmanaged<IntPtr, int>)Vst3Abi.Slot(factory, 4))(factory);
        var classes = new List<Vst3PluginClass>();
        for (var classIndex = 0; classIndex < classCount; classIndex++)
        {
            if (factory2 != IntPtr.Zero)
            {
                Vst3ClassInfo2 classInfo2;
                if (((delegate* unmanaged<IntPtr, int, Vst3ClassInfo2*, int>)Vst3Abi.Slot(factory2, 7))(factory2, classIndex, &classInfo2) == Vst3Abi.ResultOk)
                {
                    var vendor = Vst3Abi.ReadAscii(classInfo2.Vendor, 64);
                    classes.Add(new Vst3PluginClass(
                        Vst3Abi.FormatUid(new ReadOnlySpan<byte>(classInfo2.ClassId, 16)),
                        Vst3Abi.ReadAscii(classInfo2.Name, 64),
                        Vst3Abi.ReadAscii(classInfo2.Category, 32),
                        Vst3Abi.ReadAscii(classInfo2.SubCategories, 128),
                        string.IsNullOrEmpty(vendor) ? factoryVendor : vendor,
                        Vst3Abi.ReadAscii(classInfo2.Version, 64)));
                    continue;
                }
            }
            Vst3ClassInfo classInfo;
            if (((delegate* unmanaged<IntPtr, int, Vst3ClassInfo*, int>)Vst3Abi.Slot(factory, 5))(factory, classIndex, &classInfo) == Vst3Abi.ResultOk)
            {
                classes.Add(new Vst3PluginClass(
                    Vst3Abi.FormatUid(new ReadOnlySpan<byte>(classInfo.ClassId, 16)),
                    Vst3Abi.ReadAscii(classInfo.Name, 64),
                    Vst3Abi.ReadAscii(classInfo.Category, 32),
                    string.Empty,
                    factoryVendor,
                    string.Empty));
            }
        }
        Vst3Abi.Release(factory2);
        return classes;
    }

    /// <summary>
    /// Finds the factory's class for a stored id. Falls back to the name, so a
    /// project still opens if a moduleinfo.json spelled the id differently.
    /// </summary>
    public Vst3PluginClass? FindClass(string classIdentifier, string? className) =>
        Classes.FirstOrDefault(pluginClass => string.Equals(pluginClass.ClassId, classIdentifier, StringComparison.OrdinalIgnoreCase))
        ?? Classes.FirstOrDefault(pluginClass => pluginClass.IsAudioModule && className is not null && pluginClass.Name == className);

    /// <summary>factory->createInstance. Plugin host thread only.</summary>
    public IntPtr CreateInstance(string classIdentifier, byte[] interfaceId)
    {
        var classIdBytes = Vst3Abi.ParseUid(classIdentifier) ?? throw new ArgumentException("class id must be 32 hex characters", nameof(classIdentifier));
        IntPtr createdObject = IntPtr.Zero;
        fixed (byte* classIdPointer = classIdBytes)
        fixed (byte* interfaceIdPointer = interfaceId)
        {
            var createResult = ((delegate* unmanaged<IntPtr, byte*, byte*, IntPtr*, int>)Vst3Abi.Slot(Factory, 6))(Factory, classIdPointer, interfaceIdPointer, &createdObject);
            return createResult == Vst3Abi.ResultOk ? createdObject : IntPtr.Zero;
        }
    }
}
