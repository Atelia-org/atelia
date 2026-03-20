using Atelia.Data;

namespace Atelia.StateJournal;

/// <summary>
/// Commit 的文件内读取平局，内含指向 ObjectMap 帧的 <see cref="SizedPtr"/>。
/// 类似 git commit hash：拿到 CommitTicket 即可 O(1) 定位并打开对应的 commit 快照。
/// <c>default(CommitTicket)</c> 表示空/无 parent（root commit）。
/// </summary>
public readonly record struct CommitTicket(SizedPtr Ticket) {
    public bool IsNull => Ticket == default;
}
