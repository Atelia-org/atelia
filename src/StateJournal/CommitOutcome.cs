namespace Atelia.StateJournal;

/// <summary>
/// 一次 <see cref="Revision.Commit"/> 的完成状态。
/// </summary>
public enum CommitCompletion {
    /// <summary>提交完成并已更新当前 head。</summary>
    PrimaryOnly = 0,
}

/// <summary>
/// 显式描述一次 Commit 的结果。
/// </summary>
public readonly record struct CommitOutcome(
    CommitTicket HeadCommitTicket,
    CommitCompletion Completion
) {
    public bool IsPrimaryOnly => Completion == CommitCompletion.PrimaryOnly;

    public static CommitOutcome PrimaryOnly(CommitTicket commitTicket) => new(
        HeadCommitTicket: commitTicket,
        Completion: CommitCompletion.PrimaryOnly
    );
}
