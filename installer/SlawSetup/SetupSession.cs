using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using WixToolset.BootstrapperApplicationApi;

namespace SlopstackLabs.Slaw.Setup;

/// <summary>
/// Drives Burn through detect, plan and apply, and keeps the window in step.
/// Engine callbacks arrive on Burn's thread; anything visual is marshalled to
/// the dispatcher.
/// </summary>
internal sealed class SetupSession
{
    private const string PackageId = "SlawMsi";
    private const int UserCancelled = unchecked((int)0x800704C7);
    private const int UserExit = unchecked((int)0x80070642);

    private readonly SlawBootstrapper bootstrapper;
    private readonly IEngine engine;
    private readonly Display display;
    private readonly LaunchAction requestedAction;

    private Dispatcher dispatcher = null!;
    private SetupWindow window = null!;
    private IntPtr windowHandle;

    private LaunchAction plannedAction;
    private Dictionary<string, bool> plannedFeatures = new();
    private string plannedInstallFolder = "";
    private bool installed;
    private volatile bool cancelRequested;
    private int cacheProgress;
    private int executeProgress;
    private int exitCode;

    public SetupSession(SlawBootstrapper bootstrapper)
    {
        this.bootstrapper = bootstrapper;
        engine = bootstrapper.Engine;
        display = bootstrapper.Command.Display;
        requestedAction = bootstrapper.Command.Action;
    }

    private bool Interactive => display == Display.Full;

    public int Run()
    {
        dispatcher = Dispatcher.CurrentDispatcher;

        var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "slaw");
        window = new SetupWindow(ReadVariable("SlawDisplayVersion") ?? "", defaultFolder, interactive: Interactive);
        window.InstallRequested += () => Begin(LaunchAction.Install);
        window.RepairRequested += () => Begin(LaunchAction.Repair);
        window.UninstallRequested += () => Begin(LaunchAction.Uninstall);
        window.CancelRequested += RequestCancel;
        window.FinishRequested += Finish;
        window.LaunchRequested += LaunchStudio;
        window.OpenLogRequested += OpenLog;
        windowHandle = new WindowInteropHelper(window).EnsureHandle();

        bootstrapper.DetectPackageComplete += (_, e) =>
        {
            if (e.PackageId == PackageId)
            {
                installed = e.State == PackageState.Present;
            }
        };
        bootstrapper.DetectComplete += (_, e) => OnUi(() => OnDetected(e.Status));
        bootstrapper.PlanMsiFeature += (_, e) =>
        {
            if (plannedAction == LaunchAction.Install && plannedFeatures.TryGetValue(e.FeatureId, out var wanted))
            {
                e.State = wanted ? FeatureState.Local : FeatureState.Absent;
            }
        };
        bootstrapper.PlanComplete += (_, e) =>
        {
            if (e.Status >= 0)
            {
                engine.Apply(windowHandle);
            }
            else
            {
                OnUi(() => OnApplied(e.Status, restartRequired: false));
            }
        };
        bootstrapper.CacheAcquireProgress += (_, e) =>
        {
            cacheProgress = e.OverallPercentage;
            ReportProgress();
            e.Cancel = cancelRequested;
        };
        bootstrapper.ExecuteProgress += (_, e) =>
        {
            executeProgress = e.OverallPercentage;
            ReportProgress();
            e.Cancel = cancelRequested;
        };
        bootstrapper.Progress += (_, e) => e.Cancel = cancelRequested;
        bootstrapper.ApplyComplete += (_, e) =>
            OnUi(() => OnApplied(e.Status, e.Restart == ApplyRestart.RestartRequired || e.Restart == ApplyRestart.RestartInitiated));

        if (display != Display.None && display != Display.Embedded)
        {
            window.Show();
        }

        engine.Detect();
        Dispatcher.Run();
        return exitCode;
    }

    private void OnDetected(int status)
    {
        if (status < 0)
        {
            OnApplied(status, restartRequired: false);
            return;
        }

        if (!Interactive)
        {
            Begin(requestedAction);
            return;
        }

        if (installed || requestedAction == LaunchAction.Uninstall)
        {
            window.ShowMaintenance(uninstallRequested: requestedAction == LaunchAction.Uninstall);
        }
        else
        {
            window.ShowWelcome();
        }
    }

    private void Begin(LaunchAction action)
    {
        plannedAction = action;
        cacheProgress = 0;
        executeProgress = 0;

        if (action == LaunchAction.Install)
        {
            plannedInstallFolder = window.InstallFolder;
            plannedFeatures = window.FeatureSelection();
            engine.SetVariableString("InstallFolder", plannedInstallFolder, false);
        }

        window.ShowProgress(uninstalling: action == LaunchAction.Uninstall);
        engine.Plan(action);
    }

    private void ReportProgress()
    {
        var overall = plannedAction == LaunchAction.Uninstall ? executeProgress : (cacheProgress + executeProgress) / 2;
        OnUi(() => window.SetProgress(overall));
    }

    private void RequestCancel()
    {
        if (window.IsApplying)
        {
            cancelRequested = true;
            window.ShowCancelling();
        }
        else
        {
            exitCode = UserExit;
            Finish();
        }
    }

    private void OnApplied(int status, bool restartRequired)
    {
        exitCode = status;

        if (!Interactive)
        {
            Finish();
        }
        else if (status >= 0)
        {
            window.ShowComplete(uninstalled: plannedAction == LaunchAction.Uninstall, restartRequired);
        }
        else
        {
            window.ShowFailure(status, cancelled: status == UserCancelled || cancelRequested);
        }
    }

    private void LaunchStudio()
    {
        var executable = Path.Combine(plannedInstallFolder, "slaw.exe");
        if (File.Exists(executable))
        {
            Process.Start(new ProcessStartInfo(executable, "studio") { UseShellExecute = true, WorkingDirectory = plannedInstallFolder });
        }
    }

    private void OpenLog()
    {
        var log = ReadVariable("WixBundleLog");
        if (!string.IsNullOrEmpty(log) && File.Exists(log))
        {
            Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
        }
    }

    private void Finish()
    {
        window.Close();
        dispatcher.InvokeShutdown();
    }

    private string? ReadVariable(string name)
    {
        try
        {
            return engine.ContainsVariable(name) ? engine.GetVariableString(name) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnUi(Action action) => dispatcher.BeginInvoke(action);
}
