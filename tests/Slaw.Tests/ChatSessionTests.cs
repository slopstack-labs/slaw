using SlopstackLabs.Slaw.Session;
using SlopstackLabs.Slaw.Workflows;

namespace SlopstackLabs.Slaw.Tests;

/// <summary>AI chats: the co-pilot now remembers what it was told, until you start over.</summary>
public class ChatSessionTests
{
    [Fact]
    public void AutopilotPromptCarriesThePriorTurnsOfTheActiveChat()
    {
        var project = ProjectSerializer.CreateStarterProject("chat");
        var ongoingChat = new ChatSession();
        ongoingChat.Append("co-pilot", "It looks like you're making a beat.", remembered: false);
        ongoingChat.Append(ChatSession.UserSpeaker, "make the bass wobble");
        ongoingChat.Append("autopilot", "Done. Here's what I changed: • Bottom-Line Bass: lowpass sweep");
        ongoingChat.Append("system", "Inference rerouted.", remembered: false);

        var followUpPrompt = PromptLibrary.Autopilot(project, "now make it darker", Array.Empty<string>(), ongoingChat.DescribeRecentConversation());
        Assert.Contains("Conversation so far", followUpPrompt);
        Assert.Contains("you: make the bass wobble", followUpPrompt);
        Assert.Contains("autopilot: Done. Here's what I changed: • Bottom-Line Bass: lowpass sweep", followUpPrompt);
        Assert.DoesNotContain("making a beat", followUpPrompt);
        Assert.DoesNotContain("Inference rerouted", followUpPrompt);
        Assert.Equal("make the bass wobble", ongoingChat.Title);

        var freshChat = new ChatSession();
        freshChat.Append("co-pilot", "Hi! I'm your AI co-pilot.", remembered: false);
        Assert.Null(freshChat.DescribeRecentConversation());
        Assert.False(freshChat.HasConversation);
        var freshPrompt = PromptLibrary.Autopilot(project, "now make it darker", Array.Empty<string>(), freshChat.DescribeRecentConversation());
        Assert.DoesNotContain("Conversation so far", freshPrompt);
        Assert.DoesNotContain("wobble", freshPrompt);
    }

    [Fact]
    public void MemoryStaysSmallEnoughForALocalModel()
    {
        var talkativeChat = new ChatSession();
        for (var turnIndex = 0; turnIndex < 30; turnIndex++)
        {
            talkativeChat.Append(turnIndex % 2 == 0 ? ChatSession.UserSpeaker : "autopilot", $"turn {turnIndex} " + new string('a', 600));
        }
        var memory = talkativeChat.DescribeRecentConversation()!;
        var memoryLines = memory.Split('\n');
        Assert.InRange(memoryLines.Length, 1, 8);
        Assert.True(memory.Length <= 2000, $"{memory.Length} characters");
        Assert.All(memoryLines, memoryLine => Assert.True(memoryLine.Length <= 320, memoryLine));
        Assert.StartsWith("autopilot: turn 29", memoryLines[^1]);

        for (var turnIndex = 0; turnIndex < 100; turnIndex++)
        {
            talkativeChat.Append("co-pilot", "advice");
        }
        Assert.Equal(ChatSession.MaximumEntries, talkativeChat.Entries.Count);
    }

    [Fact]
    public void ArchiveRoundTripsMovesAndSurvivesCorruption()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"slaw-chats-{Guid.NewGuid():N}.json");
        try
        {
            var archive = new ChatSessionArchive();
            var savedChat = new ChatSession();
            savedChat.Append(ChatSession.UserSpeaker, "more cowbell");
            var greetingOnlyChat = new ChatSession();
            greetingOnlyChat.Append("co-pilot", "hello", remembered: false);
            archive.Track(ChatSessionArchive.UntitledProjectKey, savedChat);
            archive.Track(ChatSessionArchive.UntitledProjectKey, greetingOnlyChat);

            // Saving an untitled project takes its chats along.
            var projectKey = ChatSessionArchive.ProjectKeyFor(Path.Combine(Path.GetTempPath(), "song.slaw"));
            archive.MoveProject(ChatSessionArchive.UntitledProjectKey, projectKey);
            Assert.Empty(archive.SessionsFor(ChatSessionArchive.UntitledProjectKey));
            archive.Save(archivePath);

            var reloadedSessions = ChatSessionArchive.Load(archivePath).SessionsFor(projectKey);
            var reloadedChat = Assert.Single(reloadedSessions);
            Assert.Equal("more cowbell", reloadedChat.Title);
            Assert.Equal("you: more cowbell", reloadedChat.DescribeRecentConversation());

            File.WriteAllText(archivePath, "{\"sessionsByProject\": [not json");
            Assert.Empty(ChatSessionArchive.Load(archivePath).SessionsByProject);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }
}
