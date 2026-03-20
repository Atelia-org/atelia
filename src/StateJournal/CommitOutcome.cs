namespace Atelia.StateJournal;

/// <summary>
/// 一次 <see cref="Revision.Commit"/> 的完成状态。
/// </summary>
public enum CommitCompletion {
    /// <summary>仅完成 primary commit；未触发 compaction，或 compaction 无实际移动。</summary>
    PrimaryOnly = 0,

    /// <summary>primary commit 成功，compaction apply 与 follow-up persist 也成功。</summary>
    Compacted = 1,

    /// <summary>primary commit 成功，但 compaction 后续失败并已回滚到 primary commit 对齐状态。</summary>
    CompactionRolledBack = 2,
}

/// <summary>
/// 显式描述一次 Commit 的结果。
/// </summary>
/// <remarks>
/// 目标语义：
/// - 只有外部不可控、且被 StateJournal 视为“可诊断失败”的情况，才通过 <see cref="AteliaError"/> 建模；
/// - 纯内存 compaction / rollback 中违反内部不变量的错误属于实现 bug，应 fail-fast，而不应折叠进本结果。
///
/// 因此 <see cref="CompactionIssue"/> 仅用于承载“primary 已 durable，但 compaction 后续因非 bug 外因未完成”的诊断信息。
/// </remarks>
public readonly record struct CommitOutcome(
    CommitTicket HeadCommitTicket,
    CommitTicket PrimaryCommitTicket,
    CommitCompletion Completion,
    AteliaError? CompactionIssue = null
) {
    public bool IsCompacted => Completion == CommitCompletion.Compacted;
    public bool IsCompactionRolledBack => Completion == CommitCompletion.CompactionRolledBack;
    public bool IsPrimaryOnly => Completion == CommitCompletion.PrimaryOnly;

    public static CommitOutcome PrimaryOnly(CommitTicket commitTicket) => new(
        HeadCommitTicket: commitTicket,
        PrimaryCommitTicket: commitTicket,
        Completion: CommitCompletion.PrimaryOnly
    );

    public static CommitOutcome Compacted(CommitTicket primaryCommitTicket, CommitTicket headCommitTicket) => new(
        HeadCommitTicket: headCommitTicket,
        PrimaryCommitTicket: primaryCommitTicket,
        Completion: CommitCompletion.Compacted
    );

    public static CommitOutcome CompactionRolledBack(CommitTicket primaryCommitTicket, AteliaError issue) => new(
        HeadCommitTicket: primaryCommitTicket,
        PrimaryCommitTicket: primaryCommitTicket,
        Completion: CommitCompletion.CompactionRolledBack,
        CompactionIssue: issue
    );
}
