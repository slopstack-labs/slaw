using SlopstackLabs.Slaw.Desktop;
using SlopstackLabs.Slaw.Inference;

namespace SlopstackLabs.Slaw.Mcp;

/// <summary>
/// What the MCP tool set needs from whoever owns the project: the headless
/// <c>slaw mcp</c> process, or the running studio window. Every project access
/// goes through <see cref="InvokeAsync{T}"/>, which for the studio means the UI
/// thread — so an agent's edits are serialized with the human's.
/// </summary>
internal interface ISlawMcpHost
{
    /// <summary>"headless" or "live studio" — reported to clients in tool results.</summary>
    string HostDescription { get; }

    /// <summary>True when a studio window (and therefore live audio) is attached.</summary>
    bool IsLive { get; }

    StudioDocument Document { get; }

    /// <summary>Runs <paramref name="projectWork"/> on the thread that owns the document.</summary>
    Task<T> InvokeAsync<T>(Func<T> projectWork);

    /// <summary>Called after every successful mutating tool call (headless: autosave; live: tell the user).</summary>
    void OnAgentEdit(string clientName, string editDescription);

    /// <summary>The user's sample library.</summary>
    IReadOnlyList<string> SampleLibraryFiles { get; }

    /// <summary>slaw's own configured model, for agents that want a second opinion. Null when AI is off.</summary>
    IInferenceSubstrateProvider? InferenceProvider { get; }

    /// <summary>The studio's active AI chat, so a consulted co-pilot remembers what it was told. Headless hosts have none.</summary>
    string? RecentConversation => null;

    /// <summary>Transport control; only meaningful when <see cref="IsLive"/>. Returns a status line.</summary>
    string ControlTransport(string transportAction, int sceneIndex, int sectionIndex);
}
