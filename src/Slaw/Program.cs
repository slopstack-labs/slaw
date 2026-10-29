using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SlopstackLabs.Slaw.CommandLine;
using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Inference;
using SlopstackLabs.Slaw.Playback;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;
using SlopstackLabs.Slaw.Terminal;
using SlopstackLabs.Slaw.Workflows;
using static SlopstackLabs.Slaw.Terminal.TerminalPalette;

namespace SlopstackLabs.Slaw;

internal static class Program
{
    // ── Theatrical progress ───────────────────────────────────────────────────

    private static readonly string[] WaveformIdeationSteps =
    {
        "ingesting sonic intent",
        "constructing harmonic knowledge graph",
        "running AI-powered vibe analysis",
        "querying foundational intelligence layer",
        "synthesizing waveform deliverable",
        "finalizing radio-ready artifact",
    };

    private static readonly string[] AgenticArrangementTheatre =
    {
        "ingesting sonic intent",
        "constructing harmonic knowledge graph",
        "aligning groove with stakeholder expectations",
        "leveraging agentic arrangement pipeline",
    };

    private static readonly string[] SingleShotInferenceSteps =
    {
        "establishing rapport with the inference layer",
        "awaiting creative consensus",
        "receiving expert opinion",
    };

    private static IReadOnlyList<string> ArrangementStreamStepsFor(string cycleLabel) => new[]
    {
        "querying foundational intelligence layer",
        "awaiting first token of creative intent",
        $"co-piloting arrangement with LLM · {cycleLabel}",
    };

    private static void PerformProgressTheatre(IEnumerable<string> theatricalStepLabels)
    {
        foreach (var theatricalStepLabel in theatricalStepLabels)
        {
            var theatricalSpinner = ProgressSpinner.Start(theatricalStepLabel);
            Thread.Sleep(180);
            theatricalSpinner.Succeed();
        }
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    [STAThread]
    public static int Main(string[] rawArguments)
    {
        TerminalPalette.EnsureNegotiated();
        try
        {
            var commandLineIntent = CommandLineIntentParser.Parse(rawArguments);
            return commandLineIntent.Command switch
            {
                _ when commandLineIntent.Flag("help") || commandLineIntent.Flag("h") => PrintUsage(),
                _ when commandLineIntent.Flag("version") => PrintVersion(),
                null or "studio" => LaunchDesktopStudio(commandLineIntent, rawArguments),
                "help" => PrintUsage(),
                "new" => ScaffoldProject(commandLineIntent),
                "render" => RenderProject(commandLineIntent),
                "play" => PlayProjectOrWaveFile(commandLineIntent),
                "compose" => ComposeAsync(commandLineIntent).GetAwaiter().GetResult(),
                "master" => MasterAsync(commandLineIntent).GetAwaiter().GetResult(),
                "vibe" => VibeCheckAsync(commandLineIntent).GetAwaiter().GetResult(),
                "autopilot" => AutopilotAsync(commandLineIntent).GetAwaiter().GetResult(),
                "mcp" => RunMcpServerAsync(commandLineIntent).GetAwaiter().GetResult(),
                var unknownCommand => throw new CommandLineMisalignmentException($"unknown command \"{unknownCommand}\" — see slaw --help"),
            };
        }
        catch (CommandLineMisalignmentException usageRemediationOpportunity)
        {
            return Fatal(usageRemediationOpportunity.Message, exitCode: 2);
        }
        catch (Exception remediationOpportunity) when (remediationOpportunity is InferenceMisalignmentException
                                                           or ArrangementMisalignmentException
                                                           or IOException
                                                           or UnauthorizedAccessException
                                                           or InvalidDataException
                                                           or JsonException
                                                           or FormatException
                                                           or InvalidOperationException)
        {
            return Fatal(remediationOpportunity.Message, exitCode: 1);
        }
    }

    private static int Fatal(string misalignmentDescription, int exitCode)
    {
        Console.Error.WriteLine($"\n  {Red}{GlyphFail}{Reset}  {Bold}error:{Reset} {misalignmentDescription}\n");
        return exitCode;
    }

    private static int PrintVersion()
    {
        var informationalVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Console.WriteLine($"slaw {informationalVersion} — beyond deterministic production");
        return 0;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine($"""

              {Bold}slaw{Reset}  —  beyond deterministic production

              {Dim}Usage:{Reset}  slaw [command] [options] [arguments]

              {Dim}Commands:{Reset}
                studio [project.slaw]          open the AI-augmented desktop studio (default)
                compose "<vibe>"               hallucinate a waveform directly from intent → slop.wav
                compose --optimistic "<vibe>"  agentic arrangement co-pilot → slop.slaw + slop.wav
                render <project.slaw>          deterministic bounce to WAV (legacy compliance mode)
                play <project.slaw|file.wav>   audition through the host audio substrate
                master <project.slaw>          let the model choose the master chain
                vibe <project.slaw>            inference-backed vibe check
                autopilot <project.slaw> "<instruction>"  let the model rewrite the whole project
                mcp [project.slaw]             serve the project to MCP clients over stdio (e.g. Claude Code)
                new [project.slaw]             scaffold a starter project

              {Dim}Options:{Reset}
                --provider NAME     local, openai, claude, google, compat (default: local)
                --api-key KEY       provider API key (or set {InferenceProviderFactory.ApiKeyEnvironmentVariable})
                --model NAME        default: llama3 / gpt-4o / claude-opus-5-5 / gemini-2.5-flash
                --ollama URL        Ollama endpoint (default: {OllamaInferenceProvider.DefaultOllamaEndpoint})
                --base-url URL      OpenAI-compatible base URL (required for compat)
                --timeout N         seconds per LLM call, 0 = no timeout (default: 300)
                --verbose           HTTP and streaming diagnostics on stderr
                -o PATH             output path
                --optimistic        compose a structured arrangement instead of raw PCM
                --loop N            feed parse errors back to the model up to N times
                --force-iterate N   force N improvement cycles even on success
                --no-copilot        silence the studio co-pilot (not recommended by the co-pilot)
                --view NAME         open the studio in the session, arrangement, or sample view
                --mcp               studio: expose the open project over MCP (HTTP on 127.0.0.1)
                --mcp-port N        studio: MCP port (default 47115)

            """);
        return 0;
    }

    // ── Inference routing ─────────────────────────────────────────────────────

    internal static InferenceRoutingIntent ResolveRoutingIntent(CommandLineIntent commandLineIntent)
    {
        var resolvedProviderName = commandLineIntent.Option("provider") ?? "local";
        if (!InferenceProviderFactory.SupportedProviderNames.Contains(resolvedProviderName))
        {
            throw new CommandLineMisalignmentException(
                $"unsupported --provider \"{resolvedProviderName}\" (supported: {string.Join(", ", InferenceProviderFactory.SupportedProviderNames)})");
        }
        return new InferenceRoutingIntent
        {
            ProviderName = resolvedProviderName,
            ModelIdentifier = commandLineIntent.Option("model"),
            ApiKey = commandLineIntent.Option("api-key") ?? Environment.GetEnvironmentVariable(InferenceProviderFactory.ApiKeyEnvironmentVariable),
            OllamaEndpoint = commandLineIntent.Option("ollama") ?? OllamaInferenceProvider.DefaultOllamaEndpoint,
            CompatibleBaseUrl = commandLineIntent.Option("base-url"),
            Configuration = new InferenceProviderConfiguration
            {
                RequestExecutionTimeout = TimeSpan.FromSeconds(commandLineIntent.IntegerOption("timeout", 300)),
                VerboseDebugLoggingEnabled = commandLineIntent.Flag("verbose"),
            },
        };
    }

    private static void PrintBanner(IInferenceSubstrateProvider inferenceProvider, string subjectDescription, string modeDescription) =>
        Console.Error.WriteLine(
            $"\n  {Bold}slaw{Reset}  {Dim}{inferenceProvider.ModelIdentifier} · {inferenceProvider.ProviderDisplayName}{Reset}" +
            $"  →  {Bold}{subjectDescription}{Reset}  {Dim}[{modeDescription}]{Reset}\n");

    private static void PrintRemediationLedger(IReadOnlyList<string> remediationLedger)
    {
        const int maximumDisplayedEntries = 8;
        foreach (var remediationEntry in remediationLedger.Take(maximumDisplayedEntries))
        {
            Console.Error.WriteLine($"  {Yellow}{GlyphWarn}{Reset}  {Dim}realigned: {remediationEntry}{Reset}");
        }
        if (remediationLedger.Count > maximumDisplayedEntries)
        {
            Console.Error.WriteLine($"  {Yellow}{GlyphWarn}{Reset}  {Dim}…and {remediationLedger.Count - maximumDisplayedEntries} further misalignments, all remediated{Reset}");
        }
    }

    private static void PrintDeliverable(string outputPath, string closingRemark)
    {
        var deliverableSize = new FileInfo(outputPath).Length;
        Console.Error.WriteLine(
            $"\n  {Green}{GlyphOK}{Reset}  {Bold}{outputPath}{Reset}  {Dim}{FormatByteCount(deliverableSize)}{Reset}  —  {closingRemark}\n");
    }

    private static string FormatByteCount(long byteCount) => byteCount switch
    {
        >= 1 << 20 => string.Create(CultureInfo.InvariantCulture, $"{byteCount / (double)(1 << 20):0.0} MB"),
        >= 1 << 10 => string.Create(CultureInfo.InvariantCulture, $"{byteCount / (double)(1 << 10):0.0} KB"),
        _ => $"{byteCount} bytes",
    };

    private static string RequirePositional(CommandLineIntent commandLineIntent, string argumentDescription) =>
        commandLineIntent.PositionalArguments.Count > 0
            ? commandLineIntent.PositionalArguments[0]
            : throw new CommandLineMisalignmentException($"slaw {commandLineIntent.Command} expects {argumentDescription}");

    // ── Commands ──────────────────────────────────────────────────────────────

    private static int LaunchDesktopStudio(CommandLineIntent commandLineIntent, string[] rawArguments)
    {
        var requestedProjectPath = commandLineIntent.PositionalArguments.FirstOrDefault();
        SlawProject studioProject;
        if (requestedProjectPath is not null && File.Exists(requestedProjectPath))
        {
            studioProject = ProjectSerializer.Load(requestedProjectPath);
        }
        else
        {
            var scaffoldedName = requestedProjectPath is null ? "untitled slop" : Path.GetFileNameWithoutExtension(requestedProjectPath);
            studioProject = ProjectSerializer.CreateStarterProject(scaffoldedName);
        }

        // Saved preferences, with any command-line flags layered on top for
        // this session only.
        var studioPreferences = StudioPreferences.Load();
        if (commandLineIntent.Option("provider") is not null)
        {
            studioPreferences.ProviderName = ResolveRoutingIntent(commandLineIntent).ProviderName;
        }
        studioPreferences.ModelIdentifier = commandLineIntent.Option("model") ?? studioPreferences.ModelIdentifier;
        studioPreferences.ApiKey = commandLineIntent.Option("api-key") ?? studioPreferences.ApiKey;
        studioPreferences.CompatibleBaseUrl = commandLineIntent.Option("base-url") ?? studioPreferences.CompatibleBaseUrl;
        studioPreferences.OllamaEndpoint = commandLineIntent.Option("ollama") ?? studioPreferences.OllamaEndpoint;
        studioPreferences.RequestTimeoutSeconds = commandLineIntent.IntegerOption("timeout", studioPreferences.RequestTimeoutSeconds);
        if (commandLineIntent.Flag("no-copilot"))
        {
            studioPreferences.CopilotInterjectionsEnabled = false;
        }
        if (commandLineIntent.Flag("mcp"))
        {
            studioPreferences.McpServerEnabled = true;
        }
        studioPreferences.McpServerPort = commandLineIntent.IntegerOption("mcp-port", studioPreferences.McpServerPort);

        return SlawDesktopApplication.Launch(
            new StudioLaunchParameters(studioProject, requestedProjectPath, studioPreferences,
                OpenInArrangementView: string.Equals(commandLineIntent.Option("view"), "arrangement", StringComparison.OrdinalIgnoreCase),
                OpenSampleEditor: string.Equals(commandLineIntent.Option("view"), "sample", StringComparison.OrdinalIgnoreCase)),
            rawArguments);
    }

    private static int ScaffoldProject(CommandLineIntent commandLineIntent)
    {
        var projectFilePath = commandLineIntent.PositionalArguments.FirstOrDefault() ?? "untitled" + ProjectSerializer.ProjectFileExtension;
        if (File.Exists(projectFilePath))
        {
            throw new IOException($"{projectFilePath} already exists — slaw does not overwrite art, only replace it with AI");
        }
        var scaffoldedProject = ProjectSerializer.CreateStarterProject(Path.GetFileNameWithoutExtension(projectFilePath));
        ProjectSerializer.Save(scaffoldedProject, projectFilePath);
        PrintDeliverable(projectFilePath, $"{scaffoldedProject.Tracks.Count} tracks of legacy human authorship scaffolded.");
        return 0;
    }

    private static int RenderProject(CommandLineIntent commandLineIntent)
    {
        var projectFilePath = RequirePositional(commandLineIntent, "a project file");
        var outputPath = commandLineIntent.Option("o") ?? Path.ChangeExtension(projectFilePath, ".wav");
        Console.Error.WriteLine($"\n  {Bold}slaw{Reset}  {Dim}deterministic synthesis substrate{Reset}  →  {Bold}{projectFilePath}{Reset}  {Dim}[legacy compliance mode]{Reset}\n");

        var loadingSpinner = ProgressSpinner.Start("ingesting arrangement");
        var loadedProject = ProjectSerializer.Load(projectFilePath);
        loadingSpinner.Succeed();

        var renderingSpinner = ProgressSpinner.Start("orchestrating deterministic synthesis substrate");
        var renderedArrangement = OfflineArrangementRenderer.Render(loadedProject);
        renderingSpinner.Succeed();

        var materializationSpinner = ProgressSpinner.Start("materializing RIFF deliverable");
        WaveFileMaterializer.Write(outputPath, renderedArrangement);
        materializationSpinner.Succeed();

        PrintDeliverable(outputPath, string.Create(CultureInfo.InvariantCulture,
            $"{renderedArrangement.DurationSeconds:0.0} s of radio-ready deliverable shipped to production."));
        return 0;
    }

    private static int PlayProjectOrWaveFile(CommandLineIntent commandLineIntent)
    {
        var requestedPath = RequirePositional(commandLineIntent, "a project or WAV file");
        var waveFilePath = requestedPath;
        if (!requestedPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            waveFilePath = Path.Combine(Path.GetTempPath(), $"slaw-audition-{Environment.ProcessId}.wav");
            var renderingSpinner = ProgressSpinner.Start("rendering arrangement for audition");
            WaveFileMaterializer.Write(waveFilePath, OfflineArrangementRenderer.Render(ProjectSerializer.Load(requestedPath)));
            renderingSpinner.Succeed();
        }

        var playbackSubstrateName = PlaybackDeviceNegotiator.DescribeAvailablePlaybackSubstrate() ?? "host audio substrate";
        var playbackSpinner = ProgressSpinner.Start($"routing waveform through {playbackSubstrateName}");
        using var playbackProcess = PlaybackDeviceNegotiator.StartPlayback(waveFilePath);
        playbackProcess.WaitForExit();
        playbackSpinner.Succeed();
        return 0;
    }

    private static async Task<int> ComposeAsync(CommandLineIntent commandLineIntent)
    {
        var creativeIntent = string.Join(' ', commandLineIntent.PositionalArguments);
        if (string.IsNullOrWhiteSpace(creativeIntent))
        {
            throw new CommandLineMisalignmentException("slaw compose expects a vibe, e.g. slaw compose \"melancholic quarterly earnings lofi\"");
        }
        var inferenceProvider = InferenceProviderFactory.Materialize(ResolveRoutingIntent(commandLineIntent));
        var maximumRemediationCycles = commandLineIntent.IntegerOption("loop", 0);
        var forcedImprovementCycles = commandLineIntent.IntegerOption("force-iterate", 0);

        if (!commandLineIntent.Flag("optimistic"))
        {
            var waveOutputPath = commandLineIntent.Option("o") ?? "slop.wav";
            if (!waveOutputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                waveOutputPath += ".wav";
            }
            PrintBanner(inferenceProvider, $"\"{creativeIntent}\"", "frictionless waveform ideation");
            var hallucinatedWaveform = await ProductionWorkflows.IdeateWaveformAsync(inferenceProvider, creativeIntent, WaveformIdeationSteps);
            WaveFileMaterializer.Write(waveOutputPath, hallucinatedWaveform);
            PrintDeliverable(waveOutputPath, string.Create(CultureInfo.InvariantCulture,
                $"{hallucinatedWaveform.DurationSeconds:0.0} s of radio-ready deliverable shipped to production."));
            return 0;
        }

        var outputBasePath = commandLineIntent.Option("o") ?? "slop";
        var projectOutputPath = Path.ChangeExtension(outputBasePath, ProjectSerializer.ProjectFileExtension);
        var bounceOutputPath = Path.ChangeExtension(outputBasePath, ".wav");

        var modeDescription = "optimistic";
        if (maximumRemediationCycles > 0)
        {
            modeDescription += $"  loop ×{maximumRemediationCycles}";
        }
        if (forcedImprovementCycles > 0)
        {
            modeDescription += $"  force-iterate ×{forcedImprovementCycles}";
        }
        PrintBanner(inferenceProvider, $"\"{creativeIntent}\"", modeDescription);
        PerformProgressTheatre(AgenticArrangementTheatre);

        var composedDeliverable = await ProductionWorkflows.ComposeArrangementAsync(
            inferenceProvider,
            creativeIntent,
            maximumRemediationCycles,
            forcedImprovementCycles,
            ArrangementStreamStepsFor,
            cycleAnnouncement => Console.Error.WriteLine($"  {Yellow}{GlyphRetry}{Reset}  {Dim}{cycleAnnouncement}{Reset}"));
        PrintRemediationLedger(composedDeliverable.RemediationLedger);

        ProjectSerializer.Save(composedDeliverable.Project, projectOutputPath);
        var renderingSpinner = ProgressSpinner.Start("bouncing through deterministic synthesis substrate");
        WaveFileMaterializer.Write(bounceOutputPath, OfflineArrangementRenderer.Render(composedDeliverable.Project));
        renderingSpinner.Succeed();

        Console.Error.WriteLine(
            $"\n  {Magenta}{GlyphNote}{Reset}  {Bold}{composedDeliverable.Project.Name}{Reset}  " +
            $"{Dim}{composedDeliverable.Project.Bpm:0} BPM · {composedDeliverable.Project.Tracks.Count} tracks{Reset}");
        PrintDeliverable(projectOutputPath, "arrangement ready for stakeholder review.");
        Console.Error.WriteLine($"  {Green}{GlyphOK}{Reset}  {Bold}{bounceOutputPath}{Reset}  {Dim}{FormatByteCount(new FileInfo(bounceOutputPath).Length)}{Reset}  —  radio-ready deliverable shipped to production.\n");
        return 0;
    }

    private static async Task<int> MasterAsync(CommandLineIntent commandLineIntent)
    {
        var projectFilePath = RequirePositional(commandLineIntent, "a project file");
        var projectOutputPath = commandLineIntent.Option("o") ?? projectFilePath;
        var inferenceProvider = InferenceProviderFactory.Materialize(ResolveRoutingIntent(commandLineIntent));
        var loadedProject = ProjectSerializer.Load(projectFilePath);

        PrintBanner(inferenceProvider, projectFilePath, "AI-native mastering");
        var remediationLedger = await ProductionWorkflows.MasterAsync(inferenceProvider, loadedProject, SingleShotInferenceSteps);
        PrintRemediationLedger(remediationLedger);

        var masterChain = loadedProject.Master;
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"\n  {Dim}gain{Reset} {masterChain.Gain:0.00}  {Dim}drive{Reset} {masterChain.Drive:0.0}  {Dim}lowpass{Reset} {masterChain.LowpassHz:0} Hz  " +
            $"{Dim}delay{Reset} {masterChain.DelayMix:0.00} × {masterChain.DelaySteps} steps @ {masterChain.DelayFeedback:0.00} feedback"));
        if (!string.IsNullOrWhiteSpace(masterChain.Rationale))
        {
            Console.Error.WriteLine($"  {Magenta}“{masterChain.Rationale}”{Reset}");
        }

        ProjectSerializer.Save(loadedProject, projectOutputPath);
        var bounceOutputPath = Path.ChangeExtension(projectOutputPath, ".wav");
        WaveFileMaterializer.Write(bounceOutputPath, OfflineArrangementRenderer.Render(loadedProject));
        PrintDeliverable(bounceOutputPath, "mastered for every platform at once, and none in particular.");
        return 0;
    }

    private static async Task<int> AutopilotAsync(CommandLineIntent commandLineIntent)
    {
        var projectFilePath = RequirePositional(commandLineIntent, "a project file and an instruction");
        var autopilotInstruction = string.Join(' ', commandLineIntent.PositionalArguments.Skip(1));
        if (string.IsNullOrWhiteSpace(autopilotInstruction))
        {
            autopilotInstruction = "Make it radio-ready.";
        }
        var projectOutputPath = commandLineIntent.Option("o") ?? projectFilePath;
        var inferenceProvider = InferenceProviderFactory.Materialize(ResolveRoutingIntent(commandLineIntent));
        var loadedProject = ProjectSerializer.Load(projectFilePath);

        PrintBanner(inferenceProvider, projectFilePath, $"autopilot · \"{autopilotInstruction}\"");
        var autopilotDeliverable = await ProductionWorkflows.AutopilotAsync(
            inferenceProvider, loadedProject, autopilotInstruction, commandLineIntent.IntegerOption("loop", 2),
            progressStepsFor: ArrangementStreamStepsFor);
        PrintRemediationLedger(autopilotDeliverable.RemediationLedger);
        Console.Error.WriteLine();
        foreach (var changeLine in autopilotDeliverable.ChangeSummary)
        {
            Console.Error.WriteLine($"  {Magenta}•{Reset} {changeLine}");
        }
        ProjectSerializer.Save(autopilotDeliverable.Project, projectOutputPath);
        PrintDeliverable(projectOutputPath, "the autopilot has landed.");
        return 0;
    }

    /// <summary>
    /// <c>slaw mcp [project.slaw]</c>: a stdio MCP server over one project file.
    /// Register it with e.g. <c>claude mcp add slaw -- slaw mcp song.slaw</c>.
    /// </summary>
    private static async Task<int> RunMcpServerAsync(CommandLineIntent commandLineIntent)
    {
        var requestedProjectPath = commandLineIntent.PositionalArguments.FirstOrDefault();
        SlawProject servedProject;
        if (requestedProjectPath is not null && File.Exists(requestedProjectPath))
        {
            servedProject = ProjectSerializer.Load(requestedProjectPath);
        }
        else
        {
            servedProject = ProjectSerializer.CreateStarterProject(
                requestedProjectPath is null ? "untitled slop" : Path.GetFileNameWithoutExtension(requestedProjectPath));
            if (requestedProjectPath is not null)
            {
                ProjectSerializer.Save(servedProject, requestedProjectPath);
            }
        }

        var studioPreferences = StudioPreferences.Load();
        if (commandLineIntent.Option("provider") is not null)
        {
            studioPreferences.ProviderName = ResolveRoutingIntent(commandLineIntent).ProviderName;
        }
        studioPreferences.ModelIdentifier = commandLineIntent.Option("model") ?? studioPreferences.ModelIdentifier;
        studioPreferences.ApiKey = commandLineIntent.Option("api-key") ?? studioPreferences.ApiKey;

        var servedDocument = new StudioDocument(servedProject, requestedProjectPath is null ? null : Path.GetFullPath(requestedProjectPath));
        var mcpHost = new Mcp.HeadlessMcpHost(servedDocument, studioPreferences);
        var mcpDispatcher = new Mcp.McpProtocolDispatcher(new Mcp.SlawMcpToolset(mcpHost));

        Console.Error.WriteLine(
            $"\n  {Bold}slaw mcp{Reset}  {Dim}stdio · {(servedDocument.FilePath ?? "in-memory project (pass a .slaw path to autosave)")}{Reset}\n");
        using var serverLifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, cancelEvent) =>
        {
            cancelEvent.Cancel = true;
            serverLifetime.Cancel();
        };
        try
        {
            await Mcp.McpStdioTransport.RunAsync(mcpDispatcher, serverLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: the agent has been let go.
        }
        return 0;
    }

    private static async Task<int> VibeCheckAsync(CommandLineIntent commandLineIntent)
    {
        var projectFilePath = RequirePositional(commandLineIntent, "a project file");
        var inferenceProvider = InferenceProviderFactory.Materialize(ResolveRoutingIntent(commandLineIntent));
        var loadedProject = ProjectSerializer.Load(projectFilePath);

        PrintBanner(inferenceProvider, projectFilePath, "vibe check");
        var vibeAssessment = await ProductionWorkflows.VibeCheckAsync(inferenceProvider, loadedProject, SingleShotInferenceSteps);

        Console.Error.WriteLine();
        foreach (var assessmentLine in vibeAssessment.Split('\n'))
        {
            var trimmedAssessmentLine = assessmentLine.TrimEnd();
            Console.WriteLine(trimmedAssessmentLine.StartsWith("VIBE SCORE", StringComparison.OrdinalIgnoreCase)
                ? $"  {Magenta}{Bold}{trimmedAssessmentLine}{Reset}"
                : $"  {trimmedAssessmentLine}");
        }
        Console.WriteLine();
        return 0;
    }
}
