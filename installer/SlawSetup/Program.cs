using System;
using System.IO;
using WixToolset.BootstrapperApplicationApi;

namespace SlopstackLabs.Slaw.Setup;

internal static class Program
{
    // No [STAThread]: mbanative initializes COM as MTA on this thread. The
    // window gets its own STA thread in SlawBootstrapper.Run.
    [MTAThread]
    private static int Main()
    {
        try
        {
            ManagedBootstrapperApplication.Run(new SlawBootstrapper());
            return 0;
        }
        catch (Exception exception)
        {
            // Burn only reports that the window never connected; leave the reason somewhere findable
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "SlawSetup.log"), $"{DateTime.Now:O} {exception}{Environment.NewLine}");
            return exception.HResult;
        }
    }
}
