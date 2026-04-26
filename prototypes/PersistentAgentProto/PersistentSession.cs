using Atelia.Completion.Abstractions;
using Atelia.StateJournal;

namespace Atelia.PersistentAgentProto;

/// <summary>
/// 持久化的多轮对话会话——本原型当前的正式（且唯一）方案。
///
/// 设计原则：
/// <list type="bullet">
///   <item>外层 message 走 <c>DurableDict&lt;string&gt;</c> mixed schema，便于未来零破坏地加 typed 元数据
///         （cost / tokenUsage / toolCallId / latency 等）。</item>
///   <item>高熵文本字段集中在 <c>text: DurableDict&lt;string, string&gt;</c> 子字典，
///         typed string 当前就是 inline/non-intern 路线，可显式表达"这是不可复用、长 payload"的语义。</item>
///   <item>每条 message 写入后立即 <c>Freeze()</c>，把"消息一旦写入即不可变"的承诺写进对象状态。</item>
/// </list>
///
/// schema:
/// <code>
/// root: DurableDict&lt;string&gt;
///   "schema": int(1)
///   "systemPrompt": string
///   "createdAt": long (unix ms)
///   "messages": DurableDeque&lt;DurableDict&lt;string&gt;&gt;
///     (each, frozen after append)
///       "text": DurableDict&lt;string, string&gt;     (frozen)
///         "role": string ("user" | "assistant")
///         "content": string
///       "ts": long (unix ms)
/// </code>
/// </summary>
public sealed class PersistentSession : IDisposable {
    private const int SchemaVersion = 1;
    private const string MainBranch = "main";

    private readonly Repository _repo;
    private readonly Revision _rev;
    private readonly DurableDict<string> _root;
    private readonly DurableDeque<DurableDict<string>> _messages;

    public string SystemPrompt { get; }
    public int MessageCount => _messages.Count;

    private PersistentSession(
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

    public static PersistentSession OpenOrCreate(string repoDir, string defaultSystemPrompt) {
        var repo = Repository.OpenOrCreate(repoDir).Unwrap();
        var rev = repo.GetOrCreateBranch(MainBranch).Unwrap();
        return rev.GraphRoot is null
            ? BootstrapFresh(repo, rev, defaultSystemPrompt)
            : Reopen(repo, rev, defaultSystemPrompt);
    }

    private static PersistentSession BootstrapFresh(Repository repo, Revision rev, string systemPrompt) {
        var root = rev.CreateDict<string>();
        root.Upsert("schema", SchemaVersion);
        root.Upsert("systemPrompt", systemPrompt);
        root.Upsert("createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var messages = rev.CreateDeque<DurableDict<string>>();
        root.Upsert("messages", messages);

        repo.Commit(root).Unwrap();
        return new PersistentSession(repo, rev, root, messages, systemPrompt);
    }

    private static PersistentSession Reopen(Repository repo, Revision rev, string defaultSystemPrompt) {
        var root = rev.GetGraphRoot<DurableDict<string>>().Unwrap();
        var messages = root.GetOrThrow<DurableDeque<DurableDict<string>>>("messages")!;
        var systemPrompt = root.GetOrThrow<string>("systemPrompt") ?? defaultSystemPrompt;
        return new PersistentSession(repo, rev, root, messages, systemPrompt);
    }

    public void AppendUser(string content) => Append("user", content);
    public void AppendAssistant(string content) => Append("assistant", content);

    private void Append(string role, string content) {
        var msg = _rev.CreateDict<string>();
        var text = _rev.CreateDict<string, string>();
        text.Upsert("role", role);
        text.Upsert("content", content);
        text.Freeze();

        msg.Upsert("text", text);
        msg.Upsert("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        msg.Freeze();

        _messages.PushBack(msg);
        _repo.Commit(_root).Unwrap();
    }

    /// <summary>把持久化历史投影成 Completion 库消费的 IHistoryMessage 序列。</summary>
    public IReadOnlyList<IHistoryMessage> BuildContext() {
        var list = new List<IHistoryMessage>(_messages.Count);
        foreach (var (role, content, _) in EnumerateMessages()) {
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
            if (msg.Get("text", out DurableDict<string, string>? text) != GetIssue.None || text is null) { continue; }
            if (text.Get("role", out string? role) != GetIssue.None) { continue; }
            if (text.Get("content", out string? content) != GetIssue.None) { continue; }
            var ts = msg.GetOrThrow<long>("ts");
            yield return (role ?? string.Empty, content ?? string.Empty, ts);
        }
    }

    public void Dispose() => _repo.Dispose();
}

/// <summary>
/// 把一段纯文本包装成 IRichActionMessage，用于回灌到 OpenAI/Anthropic converter。
/// 等到我们要持久化 ToolCalls/Thinking blocks 时，这里会扩充为完整的 AggregatedAction-like record。
/// </summary>
internal sealed record TextOnlyAction(string Text) : IRichActionMessage {
    public HistoryMessageKind Kind => HistoryMessageKind.Action;
    public string Content => Text;
    public IReadOnlyList<ParsedToolCall> ToolCalls => Array.Empty<ParsedToolCall>();
    public IReadOnlyList<ActionBlock> Blocks { get; } = new ActionBlock[] { new ActionBlock.Text(Text) };
}
