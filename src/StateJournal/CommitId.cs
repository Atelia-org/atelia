using Atelia.Data;

namespace Atelia.StateJournal;

/// <summary>
/// Commit 的身份标识，内含指向 ObjectMap 帧的 <see cref="SizedPtr"/>。
/// 类似 git commit hash：拿到 CommitId 即可 O(1) 定位并打开对应的 commit 快照。
/// <c>default(CommitId)</c> 表示空/无 parent（root commit）。
/// </summary>
public readonly record struct CommitId(SizedPtr Ticket) {
    public bool IsNull => Ticket == default;
}
