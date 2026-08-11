using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCandidateCommands {
    private const int MaximumCandidateAuditEvents = 262_144;
    internal static readonly AsyncLocal<Action?> BeforeAuditCompleteForTest =
        new();
    internal static readonly AsyncLocal<int?> MaximumAuditEventsForTest =
        new();

    private static int TimelineSync(CliOptions options) {
        try {
            return TimelineSyncCore(options);
        }
        catch (SessionSelectedLineageAuditChangedException changed) {
            return Print(
                "timeline.sync",
                "raw-head-changed",
                new {
                    code = "SelectedLineageAuditChanged",
                    kind = changed.Kind.ToString(),
                    changed.ExpectedHead,
                    changed.ObservedHead
                },
                2
            );
        }
    }

    private static int TimelineSyncCore(CliOptions options) {
        options.EnsureOnly("input", "branch", "confirm-ref", "max-rows");
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        int maximumRows = ParseBoundedInt(
            options.RequireSingle("max-rows"),
            1,
            HistoryTimelineStoreLimits.MaximumRowCount,
            "--max-rows"
        );
        HistoryTimelineOpenResult opened = HistoryTimelineFactory.Open(
            engine.ReadView,
            CandidateHistoryLoadEstimator
        );
        if (opened is not HistoryTimelineOpenResult.Opened timeline) {
            return Print("timeline.sync", "open-failed", opened, 2);
        }
        using (timeline.Handle) {
            HistoryTimelineSnapshotResult snapshot = timeline.Handle.Reader
                .ReadSnapshot();
            if (snapshot is not HistoryTimelineSnapshotResult.Available current) {
                return PrintTimelineSnapshotFailure("timeline.sync", snapshot);
            }
            TimelineHeadRef expected = current.Head;
            HistoryTimelineReconcileResult reconciled = timeline.Handle
                .Coordinator.ReconcileSelectedPath(expected, engine.ReadView);
            CandidateAuditSnapshot? audit = null;
            try {
                if (reconciled is HistoryTimelineReconcileResult
                        .OfflineBootstrapRequired) {
                    CandidateAuditCaptureResult captured = CaptureAudit(engine);
                    if (captured is not CandidateAuditCaptureResult.Available
                            available) {
                        return Print(
                            "timeline.sync",
                            "audit-limit",
                            captured,
                            2
                        );
                    }
                    audit = available.Snapshot;
                    using SessionSelectedLineageForwardCursor reconcileCursor =
                        engine.OpenSelectedLineageForwardCursor(audit);
                    reconciled = timeline.Handle.Coordinator
                        .ReconcileSelectedPathOffline(
                            expected,
                            reconcileCursor
                        );
                }
                if (reconciled is HistoryTimelineReconcileResult.Unchanged same) {
                    expected = same.Head;
                }
                else if (reconciled is HistoryTimelineReconcileResult
                        .Reconciled moved) {
                    expected = moved.Head;
                }
                else {
                    return Print(
                        "timeline.sync",
                        "reconcile-failed",
                        reconciled,
                        2
                    );
                }

                int committed = 0;
                while (committed < maximumRows) {
                    OnlineSelectedRawCaptureResult raw = timeline.Handle
                        .Coordinator.CaptureOnline(expected, engine.ReadView);
                    if (raw is OnlineSelectedRawCaptureResult.Empty) {
                        return PrintSyncComplete(
                            expected,
                            committed,
                            "empty",
                            audit is null ? "online" : "offline-reconcile"
                        );
                    }
                    if (raw is not OnlineSelectedRawCaptureResult.Captured capture) {
                        return Print(
                            "timeline.sync", "capture-failed", raw, 2
                        );
                    }
                    HistoryTimelinePlanResult plan = timeline.Handle.Coordinator
                        .PlanNextRow(expected, capture.Capture);
                    if (plan is HistoryTimelinePlanResult.Selected selected) {
                        HistoryTimelineCommitResult commit = timeline.Handle
                            .Coordinator.CommitRow(selected.Candidate);
                        if (commit is not HistoryTimelineCommitResult.Committed
                                success) {
                            return Print(
                                "timeline.sync", "commit-failed", commit, 2
                            );
                        }
                        expected = success.Head;
                        committed++;
                        continue;
                    }
                    if (plan is HistoryTimelinePlanResult.NotEnough notEnough) {
                        return PrintSyncComplete(
                            expected,
                            committed,
                            "not-enough",
                            audit is null ? "online" : "offline-reconcile",
                            notEnough
                        );
                    }
                    if (plan is HistoryTimelinePlanResult.LimitExceeded limit) {
                        return Print(
                            "timeline.sync", "partition-limit", limit, 2
                        );
                    }
                    if (plan is HistoryTimelinePlanResult
                            .OfflineBootstrapRequired) {
                        if (audit is null) {
                            CandidateAuditCaptureResult captured =
                                CaptureAudit(engine);
                            if (captured is not CandidateAuditCaptureResult
                                    .Available available) {
                                return Print(
                                    "timeline.sync",
                                    "audit-limit",
                                    captured,
                                    2
                                );
                            }
                            audit = available.Snapshot;
                        }
                        return BuildOffline(
                            engine,
                            timeline.Handle,
                            audit,
                            expected,
                            committed,
                            maximumRows
                        );
                    }
                    return Print("timeline.sync", "plan-failed", plan, 2);
                }
                return Print(
                    "timeline.sync",
                    "row-limit",
                    new { head = expected, committed, maximumRows },
                    2
                );
            }
            finally {
                audit?.Dispose();
            }
        }
    }

    private static int BuildOffline(
        SessionJournalEngine engine,
        HistoryTimelineHandle timeline,
        CandidateAuditSnapshot audit,
        TimelineHeadRef expected,
        int committed,
        int maximumRows
    ) {
        using SessionSelectedLineageForwardCursor cursor =
            engine.OpenSelectedLineageForwardCursor(audit);
        if (expected.HeadRowId is { } rowId) {
            HistoryTimelineReaderRowResult selected = timeline.Reader
                .ReadSelectedRow(expected, rowId);
            if (selected is not HistoryTimelineReaderRowResult.Selected row) {
                return Print(
                    "timeline.sync", "offline-head-read-failed", selected, 2
                );
            }
            cursor.SeekToBoundary(
                row.Row.Descriptor.EndInclusive,
                row.Row.Descriptor.EndSetups
            );
        }
        HistoryTimelineOfflineBuilderOpenResult opened = timeline.Coordinator
            .OpenOfflineBuilder(expected, cursor);
        if (opened is not HistoryTimelineOfflineBuilderOpenResult.Opened builder) {
            return Print(
                "timeline.sync", "offline-open-failed", opened, 2
            );
        }
        while (committed < maximumRows) {
            HistoryTimelineOfflineStepResult step = builder.Builder
                .BuildNextRow(expected);
            if (step is HistoryTimelineOfflineStepResult.Committed success) {
                expected = success.Head;
                committed++;
                continue;
            }
            if (step is HistoryTimelineOfflineStepResult.NotEnough notEnough) {
                return PrintSyncComplete(
                    expected,
                    committed,
                    "not-enough",
                    "offline-build",
                    notEnough
                );
            }
            return Print("timeline.sync", "offline-step-failed", step, 2);
        }
        return Print(
            "timeline.sync",
            "row-limit",
            new { head = expected, committed, maximumRows },
            2
        );
    }

    private static int PrintSyncComplete(
        TimelineHeadRef head,
        int committed,
        string terminal,
        string mode,
        object? detail = null
    ) => Print(
        "timeline.sync",
        "synchronized",
        new { head, committed, terminal, mode, detail }
    );

    private static CandidateAuditCaptureResult CaptureAudit(
        SessionJournalEngine engine
    ) {
        int maximumEvents = MaximumAuditEventsForTest.Value
            ?? MaximumCandidateAuditEvents;
        if (maximumEvents is < 1 or > MaximumCandidateAuditEvents) {
            throw new InvalidOperationException(
                "The candidate audit test bound is outside the production cap."
            );
        }
        SessionSelectedLineageAuditSession audit =
            engine.BeginSelectedLineageAudit();
        var pages = new List<SessionSelectedLineageAuditPage>();
        while (!audit.IsCaptureComplete) {
            SessionSelectedLineageAuditPage page = audit.ReadNextPage(
                SessionSelectedLineageAuditLimits.MaximumPageEventCount
            );
            if (audit.EventCount > maximumEvents) {
                return new CandidateAuditCaptureResult.Limit(
                    maximumEvents
                );
            }
            pages.Add(page);
        }
        BeforeAuditCompleteForTest.Value?.Invoke();
        _ = audit.Complete();
        return new CandidateAuditCaptureResult.Available(
            new CandidateAuditSnapshot(audit.Capture, pages)
        );
    }

    private abstract record CandidateAuditCaptureResult {
        private CandidateAuditCaptureResult() { }
        internal sealed record Available(CandidateAuditSnapshot Snapshot)
            : CandidateAuditCaptureResult;
        internal sealed record Limit(int MaximumEvents)
            : CandidateAuditCaptureResult;
    }

    private sealed class CandidateAuditSnapshot(
        SessionSelectedLineageAuditCapture capture,
        IReadOnlyList<SessionSelectedLineageAuditPage> pages
    ) : ISessionSelectedLineageAuditPageSnapshot {
        public SessionSelectedLineageAuditCapture Capture { get; } = capture;
        public long PageCount => pages.Count;
        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadHeadToOldestPages() => pages;
        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadOldestToHeadPages() => pages.Reverse();
        public void Dispose() { }
    }
}
