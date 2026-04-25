using System.Text.Json;
using Atelia.Data;
using Atelia.StateJournal;

namespace Atelia.PersistentAgentProto;

/// <summary>
/// 对照实验：把每条消息整体序列化为 JSON 字符串，存到 DurableDeque&lt;string&gt;。
/// 目的：测量 typed string（仍走 intern 进 SymbolTable）路线的体积，
///       以便和 mixed dict (DurableDict&lt;string&gt; per message) 路线对比。
/// </summary>
internal sealed class JsonStringSession : IDisposable {
    private const string MainBranch = "main";
    private readonly Repository _repo;
    private readonly Revision _rev;
    private readonly DurableDict<string> _root;
    private readonly DurableDeque<string> _messages;

    public int MessageCount => _messages.Count;

    private JsonStringSession(Repository r, Revision rv, DurableDict<string> root, DurableDeque<string> msgs) {
        _repo = r; _rev = rv; _root = root; _messages = msgs;
    }

    public static JsonStringSession Create(string dir, string systemPrompt) {
        var repo = Repository.Create(dir).Unwrap();
        var rev = repo.CreateBranch(MainBranch).Unwrap();
        var root = rev.CreateDict<string>();
        root.Upsert("schema", 1);
        root.Upsert("systemPrompt", systemPrompt);
        var msgs = rev.CreateDeque<string>();
        root.Upsert("messages", msgs);
        repo.Commit(root).Unwrap();
        return new JsonStringSession(repo, rev, root, msgs);
    }

    public void Append(string role, string content) {
        var json = JsonSerializer.Serialize(new { role, content, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        _messages.PushBack(json);
        _repo.Commit(_root).Unwrap();
    }

    public void Dispose() => _repo.Dispose();
}
