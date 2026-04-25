using System.Collections.Immutable;
using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.StateJournal;

namespace Atelia.PersistentAgentProto;

/// <summary>
/// 持久化的多轮对话会话：把 system prompt + 消息序列存到 StateJournal Repository。
/// schema:
///   root: DurableDict&lt;string&gt;
///     "schema": int(1)
///     "systemPrompt": string
///     "createdAt": long
///     "messages": DurableDeque&lt;DurableDict&lt;string&gt;&gt;
///       (each)  "role" : "user" | "assistant"
///               "content" : string
///               "ts" : long (unix ms)
/// </summary>
public sealed class PersistentSession : IPersistentChatSession {
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
        var openResult = Repository.Open(repoDir);
        if (openResult.IsSuccess) {
            return Reopen(openResult.Unwrap(), defaultSystemPrompt);
        }

        var repo = Repository.Create(repoDir).Unwrap();
        return BootstrapFresh(repo, defaultSystemPrompt);
    }

    private static PersistentSession BootstrapFresh(Repository repo, string systemPrompt) {
        var rev = repo.CreateBranch(MainBranch).Unwrap();

        var root = rev.CreateDict<string>();
        root.Upsert("schema", SchemaVersion);
        root.Upsert("systemPrompt", systemPrompt);
        root.Upsert("createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var messages = rev.CreateDeque<DurableDict<string>>();
        root.Upsert("messages", messages);

        repo.Commit(root).Unwrap();

        return new PersistentSession(repo, rev, root, messages, systemPrompt);
    }

    private static PersistentSession Reopen(Repository repo, string defaultSystemPrompt) {
        var revRes = repo.CheckoutBranch(MainBranch);
        if (revRes.IsFailure) {
            // repo 存在但 main branch 不存在——bootstrap
            return BootstrapFresh(repo, defaultSystemPrompt);
        }

        var rev = revRes.Unwrap();
        var root = rev.GetGraphRoot<DurableDict<string>>().Unwrap();
        var messages = root.GetOrThrow<DurableDeque<DurableDict<string>>>("messages")!;
        var systemPrompt = root.GetOrThrow<string>("systemPrompt") ?? defaultSystemPrompt;
        return new PersistentSession(repo, rev, root, messages, systemPrompt);
    }

    public void AppendUser(string content) {
        var msg = _rev.CreateDict<string>();
        msg.Upsert("role", "user");
        msg.Upsert("content", content);
        msg.Upsert("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _messages.PushBack(msg);
        Commit();
    }

    public void AppendAssistant(string content) {
        var msg = _rev.CreateDict<string>();
        msg.Upsert("role", "assistant");
        msg.Upsert("content", content);
        msg.Upsert("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _messages.PushBack(msg);
        Commit();
    }

    private void Commit() {
        _repo.Commit(_root).Unwrap();
    }

    /// <summary>
    /// 把持久化历史转成 Completion 库可以消费的 IHistoryMessage 序列。
    /// </summary>
    public IReadOnlyList<IHistoryMessage> BuildContext() {
        var list = new List<IHistoryMessage>(_messages.Count);
        for (int i = 0; i < _messages.Count; i++) {
            if (_messages.GetAt(i, out var msg) != GetIssue.None || msg is null) { continue; }
            var role = msg.GetOrThrow<string>("role");
            var content = msg.GetOrThrow<string>("content") ?? string.Empty;
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
            var role = msg.GetOrThrow<string>("role") ?? "?";
            var content = msg.GetOrThrow<string>("content") ?? string.Empty;
            var ts = msg.GetOrThrow<long>("ts");
            yield return (role, content, ts);
        }
    }

    public void Dispose() {
        _repo.Dispose();
    }
}

/// <summary>
/// 把一段纯文本包装成 IRichActionMessage，用于回灌到 OpenAI/Anthropic converter。
/// </summary>
internal sealed record TextOnlyAction(string Text) : IRichActionMessage {
    public HistoryMessageKind Kind => HistoryMessageKind.Action;
    public string Content => Text;
    public IReadOnlyList<ParsedToolCall> ToolCalls => Array.Empty<ParsedToolCall>();
    public IReadOnlyList<ActionBlock> Blocks { get; } = new ActionBlock[] { new ActionBlock.Text(Text) };
}
