using System;
using System.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace SlopstackLabs.Slaw.Setup;

/// <summary>
/// The Burn-facing half of setup.exe. Burn calls <see cref="Run"/> once the
/// engine is connected; the UI lives on its own STA thread so engine callbacks
/// never block the turntables.
/// </summary>
internal sealed class SlawBootstrapper : BootstrapperApplication
{
    public IEngine Engine => engine;

    public IBootstrapperCommand Command { get; private set; } = null!;

    protected override void OnCreate(CreateEventArgs args)
    {
        base.OnCreate(args);
        Command = args.Command;
    }

    protected override void Run()
    {
        var exitCode = 0;
        var uiThread = new Thread(() =>
        {
            try
            {
                exitCode = new SetupSession(this).Run();
            }
            catch (Exception exception)
            {
                engine.Log(LogLevel.Error, $"slaw setup window failed: {exception}");
                exitCode = exception.HResult;
            }
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        uiThread.Join();
        engine.Quit(exitCode);
    }
}
