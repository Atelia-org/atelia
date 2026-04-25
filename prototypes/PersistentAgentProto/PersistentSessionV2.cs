using Atelia.Completion.Abstractions;
using Atelia.StateJournal;

namespace Atelia.PersistentAgentProto;

/// <summary>
/// 持久化多轮对话会话 v2：
/// 保留 PersistentSession 的结构化 schema 与易用 API，
/// 但把高熵文本字段放进 InlineString typed dict，绕过 SymbolTable。
/// schema:
///   root: DurableDict&lt;string&gt;
///     "schema": int(2)
///     "systemPrompt": string
///     "createdAt": long
///     "messages": DurableDeque&lt;DurableDict&lt;string&gt;&gt;
///       (each)
///         "text": DurableDict&lt;string, InlineString&gt;
///             "role": InlineString
///             "content": InlineString
///         "ts": long
/// </summary>
public sealed class PersistentSessionV2 : IPersistentChatSession {
    private const int SchemaVersion = 2;
    private const string MainBranch = "main";

    private readonly Repository _repo;
    private readonly Revision _rev;
    private readonly DurableDict<string> _root;
    private readonly DurableDeque<DurableDict<string>> _messages;

    public string SystemPrompt { get; }
    public int MessageCount => _messages.Count;

    private PersistentSessionV2(
        Repository repo,
        Revision rev,
        DurableDict<string> root,
        DurableDeque<DurableDict<string>> messages,
        string systemPrompt
    ) {
        _repo = repo;
        _rev = rev;
        _root = root;
        _messages = messages;
        SystemPrompt = systemPrompt;
    }

    public static PersistentSessionV2 OpenOrCreate(string repoDir, string defaultSystemPrompt) {
        var repo = Repository.OpenOrCreate(repoDir).Unwrap();
        var rev = repo.GetOrCreateBranch(MainBranch).Unwrap();

        if (rev.GraphRoot is null) {
            return BootstrapFresh(repo, rev, defaultSystemPrompt);
        }

        return Reopen(repo, rev, defaultSystemPrompt);
    }

    private static PersistentSessionV2 BootstrapFresh(Repository repo, Revision rev, string systemPrompt) {
        var root = rev.CreateDict<string>();
        root.Upsert("schema", SchemaVersion);
        root.Upsert("systemPrompt", systemPrompt);
        root.Upsert("createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var messages = rev.CreateDeque<DurableDict<string>>();
        root.Upsert("messages", messages);

        repo.Commit(root).Unwrap();

        return new PersistentSessionV2(repo, rev, root, messages, systemPrompt);
    }

    private static PersistentSessionV2 Reopen(Repository repo, Revision rev, string defaultSystemPrompt) {
        var root = rev.GetGraphRoot<DurableDict<string>>().Unwrap();
        var messages = root.GetOrThrow<DurableDeque<DurableDict<string>>>("messages")!;
        var systemPrompt = root.GetOrThrow<string>("systemPrompt") ?? defaultSystemPrompt;
        return new PersistentSessionV2(repo, rev, root, messages, systemPrompt);
    }

    public void AppendUser(string content) => Append("user", content);

    public void AppendAssistant(string content) => Append("assistant", content);

    private void Append(string role, string content) {
        var msg = _rev.CreateDict<string>();
        var text = _rev.CreateDict<string, InlineString>();
        text.Upsert("role", role);
        text.Upsert("content", content);
        msg.Upsert("text", text);
        msg.Upsert("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _messages.PushBack(msg);
        Commit();
    }

    private void Commit() {
        _repo.Commit(_root).Unwrap();
    }

    public IReadOnlyList<IHistoryMessage> BuildContext() {
        var list = new List<IHistoryMessage>(_messages.Count);
        for (int i = 0; i < _messages.Count; i++) {
            if (_messages.GetAt(i, out var msg) != GetIssue.None || msg is null) { continue; }
            if (msg.Get("text", out DurableDict<string, InlineString>? text) != GetIssue.None || text is null) { continue; }

            if (text.Get("role", out InlineString roleInline) != GetIssue.None) { continue; }
            if (text.Get("content", out InlineString contentInline) != GetIssue.None) { continue; }

            var role = roleInline.Value;
            var content = contentInline.Value;
            switch (role) {
                case "user":
                    list.Add(new ObservationMessage(content));
                    break;
                case "assistant":
                    list.Add(new TextOnlyAction(content));
                    break;
            }
        }
        return list;
    }

    public IEnumerable<(string Role, string Content, long TimestampMs)> EnumerateMessages() {
        for (int i = 0; i < _messages.Count; i++) {
            if (_messages.GetAt(i, out var msg) != GetIssue.None || msg is null) { continue; }
            if (msg.Get("text", out DurableDict<string, InlineString>? text) != GetIssue.None || text is null) { continue; }

            if (text.Get("role", out InlineString roleInline) != GetIssue.None) { continue; }
            if (text.Get("content", out InlineString contentInline) != GetIssue.None) { continue; }

            var role = roleInline.Value;
            var content = contentInline.Value;
            var ts = msg.GetOrThrow<long>("ts");
            yield return (role, content, ts);
        }
    }

    public void Dispose() {
        _repo.Dispose();
    }
}
