using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed record ChatSessionLegacyEventSourceImportResult(
    string OutputRepoDir,
    string BranchName,
    int EventCount,
    CommitAddress? HeadAddress
);

public static class ChatSessionLegacyEventSourceImporter {
    public static ChatSessionLegacyEventSourceImportResult Import(
        string inputJsonPath,
        string outputRepoDir,
        string branchName = "main"
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRepoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        if (Directory.Exists(outputRepoDir)) { throw new IOException($"Output repo directory already exists: {outputRepoDir}"); }

        var eventSource = ChatSessionLegacyEventSourceReader.Read(inputJsonPath);

        using var repo = Repository.Create(outputRepoDir).Unwrap();
        var revision = repo.CreateBranch(branchName).Unwrap();
        var root = revision.CreateDict<string>();
        var messages = revision.CreateDeque();
        root.Upsert<DurableObject>(ChatSessionStorageSchema.KeyMessages, messages);

        var state = new ReplayState(root, messages, new List<ChatSessionLegacyMessageDto>());
        for (int eventIndex = 0; eventIndex < eventSource.Events.Count; eventIndex++) {
            var replayEvent = eventSource.Events[eventIndex];
            if (replayEvent.Ordinal != eventIndex) { throw new InvalidDataException($"Event ordinal mismatch at index {eventIndex}: {replayEvent.Ordinal}."); }

            ApplyEvent(state, replayEvent);
            RewriteMessages(state.Messages, state.CurrentMessages);
            repo.Commit(state.Root, ChatSessionLegacyEventSourceProjection.BuildCommitNote(replayEvent)).Unwrap();
        }

        repo.TryGetBranchHeadAddress(branchName, out var head);
        return new ChatSessionLegacyEventSourceImportResult(outputRepoDir, branchName, eventSource.Events.Count, head);
    }

    private static void ApplyEvent(ReplayState state, ChatSessionLegacyReplayEvent replayEvent) {
        switch (replayEvent.Kind) {
            case ChatSessionLegacyEventKinds.InitialState:
                ApplyInitialState(state, replayEvent);
                break;
            case ChatSessionLegacyEventKinds.ModelTurn:
                RequireMessages(replayEvent.AppendedMessages, replayEvent.Kind).ForEach(state.CurrentMessages.Add);
                break;
            case ChatSessionLegacyEventKinds.Compaction:
                ApplyCompaction(state, replayEvent);
                break;
            case ChatSessionLegacyEventKinds.UpdateSystemPrompt:
                ApplySystemPromptChange(state, replayEvent);
                break;
            case ChatSessionLegacyEventKinds.RedundantSave:
                break;
            default:
                throw new NotSupportedException($"Event kind '{replayEvent.Kind}' is not supported by the first importer implementation.");
        }
    }

    private static void ApplyInitialState(ReplayState state, ChatSessionLegacyReplayEvent replayEvent) {
        var root = replayEvent.Root ?? throw new InvalidDataException("initial-state event is missing root metadata.");
        state.Root.Upsert(ChatSessionStorageSchema.KeyKind, root.Kind ?? ChatSessionStorageSchema.RootKind);
        state.Root.Upsert(ChatSessionStorageSchema.KeySchemaVersion, root.SchemaVersion ?? ChatSessionStorageSchema.SchemaVersion);
        if (root.ApiSpecId is not null) { state.Root.Upsert(ChatSessionStorageSchema.KeyApiSpecId, root.ApiSpecId); }
        if (root.CompletionSurfaceId is not null) { state.Root.Upsert(ChatSessionStorageSchema.KeyCompletionSurfaceId, root.CompletionSurfaceId); }
        if (root.ModelId is not null) { state.Root.Upsert(ChatSessionStorageSchema.KeyModelId, root.ModelId); }
        state.Root.Upsert(ChatSessionStorageSchema.KeySystemPrompt, root.SystemPrompt ?? string.Empty);

        state.CurrentMessages.Clear();
        state.CurrentMessages.AddRange(replayEvent.Messages ?? Array.Empty<ChatSessionLegacyMessageDto>());
    }

    private static void ApplyCompaction(ReplayState state, ChatSessionLegacyReplayEvent replayEvent) {
        var range = replayEvent.SourceRange ?? throw new InvalidDataException("compaction event is missing sourceRange.");
        var recapMessage = replayEvent.RecapMessage ?? throw new InvalidDataException("compaction event is missing recapMessage.");
        if (replayEvent.RecapSourceAnchor is not null) { recapMessage = recapMessage with { RecapSourceAnchor = replayEvent.RecapSourceAnchor }; }

        if (range.StartIndex < 0 || range.EndExclusive < range.StartIndex || range.EndExclusive > state.CurrentMessages.Count) { throw new InvalidDataException($"Invalid compaction source range [{range.StartIndex}, {range.EndExclusive}) for current message count {state.CurrentMessages.Count}."); }

        var recapIndex = replayEvent.RecapIndex ?? 0;
        if (recapIndex < 0 || recapIndex > range.EndExclusive - range.StartIndex) { throw new InvalidDataException($"Invalid compaction recapIndex {recapIndex}."); }

        var prefix = state.CurrentMessages.Take(range.StartIndex + recapIndex).ToArray();
        var suffix = state.CurrentMessages.Skip(range.EndExclusive).ToArray();
        state.CurrentMessages.Clear();
        state.CurrentMessages.AddRange(prefix);
        state.CurrentMessages.Add(recapMessage);
        state.CurrentMessages.AddRange(suffix);
    }

    private static void ApplySystemPromptChange(ReplayState state, ChatSessionLegacyReplayEvent replayEvent) {
        var change = replayEvent.SystemPromptChange ?? throw new InvalidDataException("update-system-prompt event is missing systemPromptChange.");
        state.Root.Upsert(ChatSessionStorageSchema.KeySystemPrompt, change.NewSystemPrompt ?? string.Empty);
    }

    private static List<ChatSessionLegacyMessageDto> RequireMessages(IReadOnlyList<ChatSessionLegacyMessageDto>? messages, string eventKind)
        => messages is null ? throw new InvalidDataException($"{eventKind} event is missing messages.") : messages.ToList();

    private static void RewriteMessages(DurableDeque messages, IReadOnlyList<ChatSessionLegacyMessageDto> currentMessages) {
        while (messages.Count > 0) { messages.PopBack<DurableObject>(out _); }
        for (int messageIndex = 0; messageIndex < currentMessages.Count; messageIndex++) {
            var message = currentMessages[messageIndex];
            MessageRecord.AppendHistoryMessage(
                messages,
                ChatSessionLegacyEventSourceProjection.ToHistoryMessage(message),
                ChatSessionLegacyEventSourceProjection.ParseTimestamp(message.TimestampUtc)
            );
        }
    }

    private sealed record ReplayState(
        DurableDict<string> Root,
        DurableDeque Messages,
        List<ChatSessionLegacyMessageDto> CurrentMessages
    );
}
