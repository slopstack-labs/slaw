using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Inference;
using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Synthesis;
using static SlopstackLabs.Slaw.Terminal.TerminalPalette;

namespace SlopstackLabs.Slaw.Mcp;

/// <summary>
/// The project owner for <c>slaw mcp</c>: no window, no audio, just a project
/// file that an agent edits. Every successful edit is autosaved, because an
/// agent that forgets to save is an agent that has done nothing at all.
/// </summary>
internal sealed class HeadlessMcpHost : ISlawMcpHost
{
    private readonly SemaphoreSlim projectGate = new(1, 1);
    private readonly StudioPreferences studioPreferences;
    private readonly Lazy<IReadOnlyList<string>> sampleLibraryFiles;
    private readonly Lazy<IInferenceSubstrateProvider?> inferenceProvider;

    public HeadlessMcpHost(StudioDocument document, StudioPreferences studioPreferences)
    {
        Document = document;
        this.studioPreferences = studioPreferences;
        sampleLibraryFiles = new Lazy<IReadOnlyList<string>>(() => SampleLibrary.Scan(studioPreferences.SampleFolders));
        inferenceProvider = new Lazy<IInferenceSubstrateProvider?>(() =>
        {
            if (!studioPreferences.AiFeaturesEnabled)
            {
                return null;
            }
            try
            {
                return InferenceProviderFactory.Materialize(studioPreferences.ToRoutingIntent());
            }
            catch (InferenceMisalignmentException)
            {
                return null;
            }
        });
    }

    public string HostDescription => "headless slaw mcp";

    public bool IsLive => false;

    public StudioDocument Document { get; }

    public IReadOnlyList<string> SampleLibraryFiles => sampleLibraryFiles.Value;

    public IInferenceSubstrateProvider? InferenceProvider => inferenceProvider.Value;

    public async Task<T> InvokeAsync<T>(Func<T> projectWork)
    {
        await projectGate.WaitAsync();
        try
        {
            return projectWork();
        }
        finally
        {
            projectGate.Release();
        }
    }

    public void OnAgentEdit(string clientName, string editDescription)
    {
        Console.Error.WriteLine($"  {Magenta}◆{Reset} {clientName}: {editDescription}");
        if (Document.FilePath is not { } projectFilePath)
        {
            return;
        }
        try
        {
            ProjectSerializer.Save(Document.Project, projectFilePath);
            Document.UnsavedChanges = false;
        }
        catch (Exception persistenceRemediationOpportunity) when (persistenceRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"  {Red}{GlyphFail}{Reset} autosave failed: {persistenceRemediationOpportunity.Message}");
        }
    }

    public string ControlTransport(string transportAction, int sceneIndex, int sectionIndex) =>
        "transport needs the live studio; this is the headless server";

    /// <summary>Used by tests and diagnostics: the preferences this host was created with.</summary>
    public StudioPreferences Preferences => studioPreferences;
}
