using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed partial class ChatSessionEngine : IDisposable {
    private const string RootKind = "chat-session";
    private const long SchemaVersion = 2L;
    private const string GeminiApiSpecId = "google-gemini-generate-content-v1beta";

    private const string KeyKind = "kind";
    private const string KeySchemaVersion = "schemaVersion";
    private const string KeyApiSpecId = "apiSpecId";
    private const string KeyCompletionSurfaceId = "completionSurfaceId";
    private const string KeyModelId = "modelId";
    private const string KeySystemPrompt = "systemPrompt";
    private const string KeyMessages = "messages";

    private readonly Repository _repo;
    private readonly DurableDict<string> _root;
    private readonly DurableDeque _messages;
    private ChatSessionRuntime _runtime;

    private readonly string _repoDir;
    private readonly string _branchName;
    private string _systemPrompt;

    private bool _disposed;

    private ChatSessionEngine(
        Repository repo,
        DurableDict<string> root,
        DurableDeque messages,
        ChatSessionRuntime runtime,
        string repoDir,
        string branchName
    ) {
        _repo = repo;
        _root = root;
        _messages = messages;
        _runtime = runtime;
        _repoDir = repoDir;
        _branchName = branchName;

        _systemPrompt = root.Get<string>(KeySystemPrompt, out var sp) == GetIssue.None
            ? sp! : throw new InvalidDataException("Root is missing systemPrompt.");
    }

    public string RepoDir => _repoDir;
    public string BranchName => _branchName;
    public string ModelId => _runtime.ModelId;
    public string SystemPrompt => _systemPrompt;
    public CommitAddress? PersistedHeadAddress => TryGetPersistedHeadAddress();
    public int DurableMessageCount => _messages.Count;

    /// <summary>
    /// If <paramref name="configSystemPrompt"/> differs from the currently persisted
    /// system prompt, updates the StateJournal root and the in-memory field so that
    /// future turns use the new prompt.  Returns <see langword="true"/> when a change
    /// was applied.
    /// </summary>
    public bool TrySyncSystemPrompt(string configSystemPrompt) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(configSystemPrompt);

        if (string.Equals(_systemPrompt, configSystemPrompt, StringComparison.Ordinal)) { return false; }

        DebugUtil.Info(
            "ChatSession.Persistence",
            $"TrySyncSystemPrompt: updating systemPrompt for branch={_branchName}, head={FormatCommitAddress(PersistedHeadAddress)}, oldLen={_systemPrompt.Length}, newLen={configSystemPrompt.Length}"
        );

        _root.Upsert(KeySystemPrompt, configSystemPrompt);
        _repo.Commit(_root).Unwrap();
        _systemPrompt = configSystemPrompt;
        return true;
    }

    /// <summary>
    /// Replaces the LLM connection used for subsequent turns (and compaction).
    /// Because the persisted history is provider-neutral, the same session may be
    /// continued with a different <see cref="ChatSessionRuntime"/> (client / surface /
    /// model).  Callers must serialize this with turn execution (no concurrent turns).
    /// </summary>
    public void UseRuntime(ChatSessionRuntime runtime) {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public static Task<ChatSessionEngine> CreateAsync(
        string repoDir,
        ChatSessionCreateOptions options,
        ChatSessionRuntime runtime,
        CancellationToken ct = default
    ) {
        ct.ThrowIfCancellationRequested();
        ValidateCreateArguments(runtime);

        var repo = Repository.Create(repoDir).Unwrap();
        try {
            var revision = repo.CreateBranch(options.BranchName).Unwrap();
            var root = revision.CreateDict<string>();
            root.Upsert(KeyKind, RootKind);
            root.Upsert(KeySchemaVersion, SchemaVersion);
            root.Upsert(KeyApiSpecId, runtime.CompletionClient.ApiSpecId);
            root.Upsert(KeyCompletionSurfaceId, runtime.CompletionSurfaceId);
            root.Upsert(KeyModelId, runtime.ModelId);
            root.Upsert(KeySystemPrompt, options.SystemPrompt);

            var messages = revision.CreateDeque();
            root.Upsert<DurableObject>(KeyMessages, messages);

            repo.Commit(root).Unwrap();

            var engine = new ChatSessionEngine(repo, root, messages, runtime, repoDir, options.BranchName);
            DebugUtil.Info("ChatSession.Persistence", $"CreateAsync: {engine.GetDebugStateSummary()}");
            return Task.FromResult(engine);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    public static Task<ChatSessionEngine> OpenAsync(
        string repoDir,
        ChatSessionRuntime runtime,
        string branchName = "main",
        CancellationToken ct = default
    ) {
        ct.ThrowIfCancellationRequested();

        var repo = Repository.Open(repoDir).Unwrap();
        try {
            var revision = repo.CheckoutBranch(branchName).Unwrap();

            if (revision.GraphRoot is not DurableDict<string> root) {
                repo.Dispose();
                throw new InvalidDataException("Repository graph root is not a valid chat session.");
            }

            ValidateRoot(root);
            ValidateRuntimeCompatibility(runtime);

            if (!root.TryGet<DurableDeque>(KeyMessages, out var messages) || messages is null) {
                repo.Dispose();
                throw new InvalidDataException("Root is missing messages deque.");
            }

            var engine = new ChatSessionEngine(repo, root, messages, runtime, repoDir, branchName);
            DebugUtil.Info("ChatSession.Persistence", $"OpenAsync: {engine.GetDebugStateSummary()}");
            return Task.FromResult(engine);
        }
        catch {
            repo.Dispose();
            throw;
        }
    }

    private static void ValidateCreateArguments(ChatSessionRuntime runtime) {
        if (runtime.CompletionClient.ApiSpecId == GeminiApiSpecId
            && runtime.ToolSession.VisibleDefinitions.Length > 0) { throw new NotSupportedException("Gemini tool loop is not supported in ChatSession v1."); }
    }

    private static void ValidateRuntimeCompatibility(ChatSessionRuntime runtime) {
        if (runtime.CompletionClient.ApiSpecId == GeminiApiSpecId
            && runtime.ToolSession.VisibleDefinitions.Length > 0) { throw new NotSupportedException("Gemini tool loop is not supported in ChatSession v1."); }
    }

    private static void ValidateRoot(DurableDict<string> root) {
        if (root.Get<string>(KeyKind, out var kind) != GetIssue.None || kind != RootKind) { throw new InvalidDataException("Root is not a chat-session."); }

        if (root.Get<long>(KeySchemaVersion, out var version) != GetIssue.None || version != SchemaVersion) { throw new InvalidDataException($"Unsupported schema version. Expected {SchemaVersion}."); }
    }

    private void Commit() {
        ThrowIfDisposed();
        var beforeHead = PersistedHeadAddress;
        DebugUtil.Info(
            "ChatSession.Persistence",
            $"Commit before: branch={_branchName}, head={FormatCommitAddress(beforeHead)}, durableMessages={_messages.Count}"
        );
        _repo.Commit(_root).Unwrap();
        DebugUtil.Info(
            "ChatSession.Persistence",
            $"Commit after: branch={_branchName}, head={FormatCommitAddress(PersistedHeadAddress)}, durableMessages={_messages.Count}"
        );
    }

    public IReadOnlyList<IHistoryMessage> Context => MessageRecord.ToHistoryMessages(_messages);

    public ChatSessionStatistics GetStatistics() {
        ThrowIfDisposed();
        var allMessages = MessageRecord.ToHistoryMessages(_messages);
        int obsCount = 0, actionCount = 0, toolCount = 0, recapCount = 0;
        for (int i = 0; i < allMessages.Count; i++) {
            switch (allMessages[i].Kind) {
                case HistoryMessageKind.Observation:
                    obsCount++;
                    break;
                case HistoryMessageKind.Action:
                    actionCount++;
                    break;
                case HistoryMessageKind.ToolResults:
                    toolCount++;
                    break;
            }
        }

        for (int i = 0; i < _messages.Count; i++) {
            if (_messages.TryGetAt<DurableDict<string>>(i, out var record) && record is not null) {
                if (record.TryGet<string>(MessageRecord.KeyKind, out var kind) && kind == MessageRecord.KindRecap) { recapCount++; }
            }
        }

        return new ChatSessionStatistics(
            MessageCount: allMessages.Count,
            ObservationCount: obsCount,
            ActionCount: actionCount,
            ToolResultsCount: toolCount,
            RecapCount: recapCount,
            EstimatedTokens: ChatSessionTokenEstimator.Estimate(allMessages)
        );
    }

    private void ThrowIfDisposed() {
        if (_disposed) { throw new ObjectDisposedException(nameof(ChatSessionEngine)); }
    }

    public void Dispose() {
        if (_disposed) { return; }
        _disposed = true;
        _repo.Dispose();
    }

    public string GetDebugStateSummary() {
        var stats = GetStatistics();
        return $"repoDir={_repoDir}, branch={_branchName}, head={FormatCommitAddress(PersistedHeadAddress)}, durableMessages={_messages.Count}, context={stats.MessageCount}, obs={stats.ObservationCount}, action={stats.ActionCount}, tool={stats.ToolResultsCount}, recap={stats.RecapCount}, tokens={stats.EstimatedTokens}";
    }

    private CommitAddress? TryGetPersistedHeadAddress() {
        ThrowIfDisposed();
        return _repo.TryGetBranchHeadAddress(_branchName, out var headAddress) ? headAddress : null;
    }

    private static string FormatCommitAddress(CommitAddress? address)
        => address?.ToString() ?? "<unborn>";
}

internal static class ChatSessionTokenEstimator {
    public static ulong Estimate(IReadOnlyList<IHistoryMessage> messages) {
        ulong total = 0;
        for (int i = 0; i < messages.Count; i++) {
            total += Estimate(messages[i]);
        }
        return total;
    }

    public static ulong Estimate(IHistoryMessage message) {
        return message switch {
            ActionMessage action => EstimateAction(action),
            ObservationMessage obs => (ulong)(obs.Content?.Length ?? 0) / 2,
            _ => 0
        };
    }

    private static ulong EstimateAction(ActionMessage action) {
        ulong total = (ulong)action.GetFlattenedText().Length / 2;
        var toolCalls = action.ToolCalls;
        for (int i = 0; i < toolCalls.Count; i++) {
            total += (ulong)toolCalls[i].RawArgumentsJson.Length / 2;
        }
        return total;
    }
}
