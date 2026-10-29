using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SlopstackLabs.Slaw.Workflows;

namespace SlopstackLabs.Slaw.Desktop;

/// <summary>
/// The AI panel's chats. Each project keeps its own sessions; the active one
/// is what the panel shows and what autopilot, compose, vibe check and the
/// co-pilot are reminded of. "＋ New chat" (Ctrl+Shift+N) wipes the model's
/// memory, an experience otherwise reserved for reorganizations.
/// </summary>
internal sealed partial class StudioWindow
{
    private readonly ChatSessionArchive chatArchive = ChatSessionArchive.Load();
    private readonly ComboBox chatSessionSelector = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "this chat" };
    private readonly DispatcherTimer chatArchiveSaveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private List<ChatSession> listedChatSessions = new();
    private ChatSession activeChat = new();
    private string chatProjectKey = ChatSessionArchive.UntitledProjectKey;
    private bool chatSelectorUpdating;
    private bool chatInitialized;

    /// <summary>Restores the open project's latest chat, or starts (and greets) a new one.</summary>
    private void InitializeChat()
    {
        chatArchiveSaveTimer.Tick += (_, _) =>
        {
            chatArchiveSaveTimer.Stop();
            chatArchive.Save();
        };
        Closed += (_, _) =>
        {
            chatArchiveSaveTimer.Stop();
            chatArchive.Save();
        };
        chatSessionSelector.SelectionChanged += (_, _) =>
        {
            if (!chatSelectorUpdating && chatSessionSelector.SelectedIndex >= 0 && chatSessionSelector.SelectedIndex < listedChatSessions.Count &&
                !ReferenceEquals(listedChatSessions[chatSessionSelector.SelectedIndex], activeChat))
            {
                ActivateChat(listedChatSessions[chatSessionSelector.SelectedIndex]);
            }
        };
        chatProjectKey = ChatSessionArchive.ProjectKeyFor(document.FilePath);
        chatInitialized = true;
        RestoreOrStartChat();
    }

    private Control ComposeChatSessionBar()
    {
        var newChatButton = StudioUi.Button("＋ New chat", StartNewChat, "Start a fresh conversation: the AI forgets this chat (Ctrl+Shift+N)", compact: true);
        ToolTip.SetTip(chatSessionSelector, "Earlier chats about this project. Pick one to continue it where you left off.");
        var sessionBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,Auto") };
        sessionBar.Children.Add(chatSessionSelector);
        Grid.SetColumn(newChatButton, 2);
        sessionBar.Children.Add(newChatButton);
        return sessionBar;
    }

    /// <summary>Begins a new chat for the open project, with a fresh greeting and no memory.</summary>
    private void StartNewChat()
    {
        // Leaving an empty chat behind would only clutter the switcher.
        if (chatArchive.SessionsByProject.TryGetValue(chatProjectKey, out var projectSessions) && !activeChat.HasConversation)
        {
            projectSessions.Remove(activeChat);
        }
        var freshChat = new ChatSession();
        chatArchive.Track(chatProjectKey, freshChat);
        ActivateChat(freshChat);
        if (AiFeaturesEnabled)
        {
            AppendCopilotMessage("co-pilot", preferences.CopilotInterjectionsEnabled
                ? CopilotGreetings[Random.Shared.Next(CopilotGreetings.Length)]
                : "Unsolicited advice is disabled. I will only speak when spoken to, which is new for me.", remembered: false);
        }
        editsSinceLastInterjection = 0;
        SetStatus(StatusSeverity.Informational, "new chat started. the AI has no recollection of previous discussions, as is customary.");
    }

    private void RestoreOrStartChat()
    {
        if (chatArchive.SessionsFor(chatProjectKey).FirstOrDefault(session => session.HasConversation) is { } latestChat)
        {
            ActivateChat(latestChat);
        }
        else
        {
            StartNewChat();
        }
    }

    private void ActivateChat(ChatSession chat)
    {
        activeChat = chat;
        chatArchive.Track(chatProjectKey, chat);
        copilotTranscriptPanel.Children.Clear();
        foreach (var entry in chat.Entries)
        {
            copilotTranscriptPanel.Children.Add(ComposeChatBubble(entry));
        }
        RefreshChatSessionSelector();
        Dispatcher.UIThread.Post(() => copilotTranscriptScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// <summary>Called whenever the window chrome refreshes: a different project file means a different set of chats.</summary>
    private void SynchronizeChatWithProject()
    {
        var currentProjectKey = ChatSessionArchive.ProjectKeyFor(document.FilePath);
        if (!chatInitialized || currentProjectKey == chatProjectKey)
        {
            return;
        }
        chatProjectKey = currentProjectKey;
        RestoreOrStartChat();
        ScheduleChatArchiveSave();
    }

    /// <summary>Save As: the conversation follows the project to its new file.</summary>
    private void CarryChatsToSavedPath(string? previousFilePath)
    {
        var savedProjectKey = ChatSessionArchive.ProjectKeyFor(document.FilePath);
        var previousProjectKey = ChatSessionArchive.ProjectKeyFor(previousFilePath);
        chatArchive.Track(previousProjectKey, activeChat);
        chatArchive.MoveProject(previousProjectKey, savedProjectKey);
        chatProjectKey = savedProjectKey;
        ScheduleChatArchiveSave();
    }

    /// <summary>Adds an entry to <paramref name="targetChat"/> (default: the active one) and shows it if that chat is on screen.</summary>
    private void RecordChatMessage(string speaker, string message, bool remembered, ChatSession? targetChat = null)
    {
        targetChat ??= activeChat;
        var previousTitle = targetChat.Title;
        var recordedEntry = targetChat.Append(speaker, message, remembered && speaker is not ("system" or "mcp"));
        if (ReferenceEquals(targetChat, activeChat))
        {
            copilotTranscriptPanel.Children.Add(ComposeChatBubble(recordedEntry));
            while (copilotTranscriptPanel.Children.Count > ChatSession.MaximumEntries)
            {
                copilotTranscriptPanel.Children.RemoveAt(0);
            }
            Dispatcher.UIThread.Post(() => copilotTranscriptScroller.ScrollToEnd(), DispatcherPriority.Background);
        }
        if (targetChat.Title != previousTitle || (recordedEntry.Remembered && targetChat.Entries.Count(entry => entry.Remembered) == 1))
        {
            RefreshChatSessionSelector();
        }
        ScheduleChatArchiveSave();
    }

    private static Control ComposeChatBubble(ChatEntry entry)
    {
        var speakerBrush = entry.Speaker switch
        {
            "system" => StudioTheme.Warning,
            ChatSession.UserSpeaker => StudioTheme.Drum,
            _ => StudioTheme.Accent,
        };
        return new Border
        {
            Background = StudioTheme.SurfaceRaised,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = $"{entry.Speaker} · {entry.Timestamp:HH:mm}", FontSize = 10, FontWeight = FontWeight.Bold, Foreground = speakerBrush },
                    new SelectableTextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, Foreground = StudioTheme.Text, FontSize = 12 },
                },
            },
        };
    }

    private void RefreshChatSessionSelector()
    {
        listedChatSessions = chatArchive.SessionsFor(chatProjectKey)
            .Where(session => session.HasConversation || ReferenceEquals(session, activeChat))
            .ToList();
        chatSelectorUpdating = true;
        try
        {
            chatSessionSelector.ItemsSource = listedChatSessions.Select(ChatSessionArchive.Describe).ToList();
            chatSessionSelector.SelectedIndex = listedChatSessions.IndexOf(activeChat);
        }
        finally
        {
            chatSelectorUpdating = false;
        }
    }

    private void ScheduleChatArchiveSave()
    {
        if (!chatArchiveSaveTimer.IsEnabled)
        {
            chatArchiveSaveTimer.Start();
        }
    }
}
