using System.Text.Json;
using System.Text.Json.Serialization;
using SlopstackLabs.Slaw.Session;

namespace SlopstackLabs.Slaw.Workflows;

/// <summary>One line of an AI chat: who said it, what, and when.</summary>
internal sealed class ChatEntry
{
    public string Speaker { get; set; } = "co-pilot";
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>False for greetings and system notices: shown in the transcript, never fed back to the model.</summary>
    public bool Remembered { get; set; } = true;
}

/// <summary>
/// One conversation with the studio's AI. The transcript is what the panel
/// shows; the recent remembered entries are what the model is reminded of, so
/// "now make it darker" refers to whatever "it" was a minute ago.
/// </summary>
internal sealed class ChatSession
{
    public const int MaximumEntries = 80;
    public const string UntitledTitle = "New chat";
    public const string UserSpeaker = "you";

    // Small local models get a short memory. So do most stakeholders.
    private const int RememberedEntryCount = 8;
    private const int MaximumCharactersPerEntry = 300;
    private const int MaximumMemoryCharacters = 2000;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = UntitledTitle;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public List<ChatEntry> Entries { get; set; } = new();

    /// <summary>Whether anything in this chat is worth remembering (a greeting alone is not).</summary>
    [JsonIgnore]
    public bool HasConversation => Entries.Any(entry => entry.Remembered);

    public ChatEntry Append(string speaker, string text, bool remembered = true)
    {
        var appendedEntry = new ChatEntry { Speaker = speaker, Text = text, Remembered = remembered };
        Entries.Add(appendedEntry);
        if (Entries.Count > MaximumEntries)
        {
            Entries.RemoveRange(0, Entries.Count - MaximumEntries);
        }
        if (Title == UntitledTitle && speaker == UserSpeaker && !string.IsNullOrWhiteSpace(text))
        {
            Title = Abbreviate(text, 40);
        }
        return appendedEntry;
    }

    /// <summary>
    /// The last few remembered entries, oldest first, as "speaker: text" lines,
    /// each and all of them length-capped. Null when there is nothing to remember.
    /// </summary>
    public string? DescribeRecentConversation()
    {
        var memoryLines = new List<string>();
        var memoryLength = 0;
        foreach (var entry in Entries.Where(entry => entry.Remembered).Reverse().Take(RememberedEntryCount))
        {
            var memoryLine = $"{entry.Speaker}: {Abbreviate(entry.Text, MaximumCharactersPerEntry)}";
            if (memoryLength + memoryLine.Length > MaximumMemoryCharacters)
            {
                break;
            }
            memoryLines.Add(memoryLine);
            memoryLength += memoryLine.Length + 1;
        }
        if (memoryLines.Count == 0)
        {
            return null;
        }
        memoryLines.Reverse();
        return string.Join('\n', memoryLines);
    }

    private static string Abbreviate(string text, int maximumLength)
    {
        var singleLineText = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLineText.Length <= maximumLength ? singleLineText : singleLineText[..(maximumLength - 1)].TrimEnd() + "…";
    }
}

/// <summary>
/// Every project's chats, persisted next to the studio settings so reopening a
/// project reopens the conversation about it. A corrupt archive is a fresh start.
/// </summary>
internal sealed class ChatSessionArchive
{
    /// <summary>Chats about a project that has never been saved.</summary>
    public const string UntitledProjectKey = "(untitled)";

    private const int MaximumSessionsPerProject = 20;
    private const int MaximumProjects = 50;

    public Dictionary<string, List<ChatSession>> SessionsByProject { get; set; } = new();

    public static string ArchiveFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "slaw", "chats.json");

    public static string ProjectKeyFor(string? projectFilePath) =>
        string.IsNullOrEmpty(projectFilePath) ? UntitledProjectKey : Path.GetFullPath(projectFilePath);

    /// <summary>A project's chats, most recent first.</summary>
    public IReadOnlyList<ChatSession> SessionsFor(string projectKey) =>
        SessionsByProject.TryGetValue(projectKey, out var projectSessions)
            ? projectSessions.OrderByDescending(session => session.Entries.LastOrDefault()?.Timestamp ?? session.StartedAt).ToList()
            : Array.Empty<ChatSession>();

    /// <summary>Files a session under a project (once); empty chats are kept out of the archive until they say something.</summary>
    public void Track(string projectKey, ChatSession session)
    {
        if (!SessionsByProject.TryGetValue(projectKey, out var projectSessions))
        {
            projectSessions = new List<ChatSession>();
            SessionsByProject[projectKey] = projectSessions;
        }
        if (!projectSessions.Contains(session))
        {
            projectSessions.Add(session);
        }
    }

    /// <summary>Save As: the chats move with the project to its new file.</summary>
    public void MoveProject(string sourceProjectKey, string destinationProjectKey)
    {
        if (sourceProjectKey == destinationProjectKey || !SessionsByProject.Remove(sourceProjectKey, out var movedSessions))
        {
            return;
        }
        foreach (var session in movedSessions)
        {
            Track(destinationProjectKey, session);
        }
    }

    public static ChatSessionArchive Load(string? archiveFilePath = null)
    {
        archiveFilePath ??= ArchiveFilePath;
        try
        {
            if (File.Exists(archiveFilePath))
            {
                var loadedArchive = JsonSerializer.Deserialize<ChatSessionArchive>(File.ReadAllText(archiveFilePath), ProjectSerializer.LenientSerializationOptions);
                if (loadedArchive?.SessionsByProject is not null)
                {
                    foreach (var projectSessions in loadedArchive.SessionsByProject.Values)
                    {
                        projectSessions.RemoveAll(session => session?.Entries is null);
                    }
                    return loadedArchive;
                }
            }
        }
        catch (Exception archiveRemediationOpportunity) when (archiveRemediationOpportunity is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unreadable chat history is an opportunity to start the conversation over.
        }
        return new ChatSessionArchive();
    }

    public void Save(string? archiveFilePath = null)
    {
        archiveFilePath ??= ArchiveFilePath;
        var persistedSessions = SessionsByProject
            .Select(pair => (ProjectKey: pair.Key, Sessions: pair.Value
                .Where(session => session.HasConversation)
                .OrderByDescending(session => session.Entries.LastOrDefault()?.Timestamp ?? session.StartedAt)
                .Take(MaximumSessionsPerProject)
                .ToList()))
            .Where(project => project.Sessions.Count > 0)
            .OrderByDescending(project => project.Sessions[0].Entries.LastOrDefault()?.Timestamp ?? project.Sessions[0].StartedAt)
            .Take(MaximumProjects)
            .ToDictionary(project => project.ProjectKey, project => project.Sessions);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(archiveFilePath)!);
            var temporaryFilePath = archiveFilePath + ".tmp";
            File.WriteAllText(temporaryFilePath, JsonSerializer.Serialize(new ChatSessionArchive { SessionsByProject = persistedSessions }, ProjectSerializer.LenientSerializationOptions));
            File.Move(temporaryFilePath, archiveFilePath, overwrite: true);
        }
        catch (Exception archiveRemediationOpportunity) when (archiveRemediationOpportunity is IOException or UnauthorizedAccessException)
        {
            // Chats are a courtesy; a full disk is not a reason to interrupt production.
        }
    }

    /// <summary>A display label for the session switcher: "Make the bass wobble · Oct 29 14:02".</summary>
    public static string Describe(ChatSession session) =>
        $"{session.Title} · {(session.Entries.LastOrDefault()?.Timestamp ?? session.StartedAt).ToString("MMM d HH:mm", System.Globalization.CultureInfo.InvariantCulture)}";
}
