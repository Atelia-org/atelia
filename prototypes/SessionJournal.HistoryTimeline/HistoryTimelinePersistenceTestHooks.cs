namespace Atelia.SessionJournal.HistoryTimeline;

/// <summary>
/// Process-death failpoints owned by the persistence tests. Production
/// factory and maintenance entry points always use <see cref="None"/>.
/// </summary>
internal sealed record HistoryTimelinePersistenceTestHooks(
    Action? BeforePutPolicyCommit = null,
    Action? AfterPutPolicyCommit = null,
    Action? BeforePolicyCasCommit = null,
    Action? AfterPolicyCasCommit = null,
    Action? BeforeAppendCommit = null,
    Action? AfterAppendCommit = null,
    Action? AfterAppendWriterLockAcquired = null,
    Action? BeforeReconcileCommit = null,
    Action? AfterReconcileCommit = null,
    Action? BeforeLocatorCreatePublish = null,
    Action? AfterLocatorCreatePublish = null,
    Action? BeforeLocatorAbandonPublish = null,
    Action? AfterLocatorAbandonPublish = null,
    Action? AfterBackupCopyBeforeVerify = null,
    Action? BeforeBackupPublish = null,
    Action? AfterBackupPublish = null,
    Action? AfterRestoreCopyBeforeVerify = null,
    Action? BeforeRestoreReplace = null,
    Action? AfterRestoreReplace = null,
    Action? AfterBoundaryProbeOpened = null,
    Action? BeforeBoundaryProbeLookupQuery = null,
    Action? AfterLifetimeClosing = null
) {
    internal static HistoryTimelinePersistenceTestHooks None { get; }
        = new();
}
