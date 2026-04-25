using Atelia.StateJournal;

namespace Atelia.PersistentAgentProto;

/// <summary>
/// 对照实验 #3：InlineString 路线。
/// schema: messages = DurableDeque&lt;DurableDict&lt;string, InlineString&gt;&gt;
///   每条 message 是 typed dict，role/content 都用 InlineString 存（绕过 SymbolTable intern）。
///   ts 单独放外层数组太麻烦，直接放 InlineString("&lt;ts ms&gt;")——毕竟原型只关心持久化体积。
/// 与 PersistentSession (mixed dict) / JsonStringSession (typed deque + JSON) 对比。
/// </summary>
internal sealed class InlineSession : IDisposable {
    private const string MainBranch = "main";
    private readonly Repository _repo;
    private readonly Revision _rev;
    private readonly DurableDict<string> _root;
    private readonly DurableDeque<DurableDict<string, InlineString>> _messages;

    public int MessageCount => _messages.Count;

    private InlineSession(
        Repository repo,
        Revision rev,
        DurableDict<string> root,
        DurableDeque<DurableDict<string, InlineString>> messages
    ) {
        _repo = repo;
        _rev = rev;
        _root = root;
        _messages = messages;
    }

    public static InlineSession Create(string dir, string systemPrompt) {
        var repo = Repository.Create(dir).Unwrap();
        var rev = repo.CreateBranch(MainBranch).Unwrap();
        var root = rev.CreateDict<string>();
        root.Upsert("schema", 1);
        root.Upsert("systemPrompt", systemPrompt);
        var messages = rev.CreateDeque<DurableDict<string, InlineString>>();
        root.Upsert("messages", messages);
        repo.Commit(root).Unwrap();
        return new InlineSession(repo, rev, root, messages);
    }

    public void Append(string role, string content) {
        var msg = _rev.CreateDict<string, InlineString>();
        msg.Upsert("role", new InlineString(role));
        msg.Upsert("content", new InlineString(content));
        msg.Upsert("ts", new InlineString(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
        _messages.PushBack(msg);
        _repo.Commit(_root).Unwrap();
    }

    public void Dispose() => _repo.Dispose();
}
