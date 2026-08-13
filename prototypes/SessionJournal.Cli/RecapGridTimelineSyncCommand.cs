using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    private const int MaximumRecapGridAuditEvents =
        HistoryRecentReserveOperationLimits.MaximumRawEvents;
    private const int MaximumTimelineRowsPerSync = 1_000_000;
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
        using SessionJournalEngine engine = OpenMutableBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        int maximumRows = ParseBoundedInt(
            options.RequireSingle("max-rows"),
            1,
            MaximumTimelineRowsPerSync,
            "--max-rows"
        );
        HistoryTimelineOpenResult opened = HistoryTimelineFactory.Open(
            engine.ReadView,
            RecapGridHistoryLoadEstimator
        );
        if (opened is not HistoryTimelineOpenResult.Opened timeline) {
            return Print("timeline.sync", "open-failed", opened, 2);
        }
        using (timeline.Handle) {
            RecapGridCadenceOpenResult cadenceOpened =
                RecapGridCadenceFactory.OpenMutable(engine);
            if (cadenceOpened is not RecapGridCadenceOpenResult.Opened
                    cadence) {
                return Print("timeline.sync", "cadence-open-failed",
                    cadenceOpened, 2);
            }
            using (cadence.Handle) {
                RecapGridCadenceTimelineSealOpenResult sealOpened =
                    cadence.Handle.BeginTimelineSeal(timeline.Handle);
                if (sealOpened is not
                        RecapGridCadenceTimelineSealOpenResult.Opened seal) {
                    return Print("timeline.sync", "seal-open-failed",
                        sealOpened, 2);
                }
                using RecapGridCadenceTimelineSealOperation sealOperation =
                    seal.Operation;
                TimelineHeadRef expected = sealOperation.HeadAtOpen;
            HistoryTimelineReconcileResult reconciled = timeline.Handle
                .Coordinator.ReconcileSelectedPath(expected, engine.ReadView);
            RecapGridCadenceOfflineAudit? audit = null;
            try {
                if (reconciled is HistoryTimelineReconcileResult
                        .OfflineBootstrapRequired) {
                    RecapGridCadenceOfflineAuditCaptureResult captured =
                        CaptureAudit(sealOperation);
                    if (captured is not
                            RecapGridCadenceOfflineAuditCaptureResult.Available
                            available) {
                        return Print(
                            "timeline.sync",
                            "audit-limit",
                            captured,
                            2
                        );
                    }
                    audit = available.Audit;
                    reconciled = sealOperation.ReconcileSelectedPathOffline(
                        expected,
                        audit);
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
                    HistoryTimelinePlanResult plan = sealOperation
                        .PlanNextRow(expected, capture.Capture);
                    if (plan is HistoryTimelinePlanResult.Selected selected) {
                        HistoryTimelineCommitResult commit = sealOperation
                            .CommitRow(selected.Candidate);
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
                    if (plan is HistoryTimelinePlanResult
                            .RecentReserveNotReached reserve) {
                        return PrintSyncComplete(
                            expected,
                            committed,
                            "recent-reserve-not-reached",
                            audit is null ? "online" : "offline-reconcile",
                            reserve);
                    }
                    if (plan is HistoryTimelinePlanResult.LimitExceeded limit) {
                        return Print(
                            "timeline.sync", "partition-limit", limit, 2
                        );
                    }
                    if (plan is HistoryTimelinePlanResult
                            .OfflineBootstrapRequired) {
                        if (audit is null) {
                            RecapGridCadenceOfflineAuditCaptureResult captured =
                                CaptureAudit(sealOperation);
                            if (captured is not
                                    RecapGridCadenceOfflineAuditCaptureResult
                                    .Available available) {
                                return Print(
                                    "timeline.sync",
                                    "audit-limit",
                                    captured,
                                    2
                                );
                            }
                            audit = available.Audit;
                        }
                        return BuildOffline(
                            sealOperation,
                            audit,
                            expected,
                            committed,
                            maximumRows
                        );
                    }
                    return Print("timeline.sync", "plan-failed", plan, 2);
                }
                OnlineSelectedRawCaptureResult terminalRaw = timeline.Handle
                    .Coordinator.CaptureOnline(expected, engine.ReadView);
                if (terminalRaw is OnlineSelectedRawCaptureResult.Empty) {
                    return PrintSyncComplete(
                        expected,
                        committed,
                        "empty",
                        audit is null ? "online" : "offline-reconcile");
                }
                if (terminalRaw is not
                        OnlineSelectedRawCaptureResult.Captured terminalCapture) {
                    return Print(
                        "timeline.sync", "capture-failed", terminalRaw, 2);
                }
                HistoryTimelinePlanResult terminalPlan = sealOperation
                    .PlanNextRow(expected, terminalCapture.Capture);
                if (terminalPlan is HistoryTimelinePlanResult.NotEnough
                        terminalNotEnough) {
                    return PrintSyncComplete(
                        expected,
                        committed,
                        "not-enough",
                        audit is null ? "online" : "offline-reconcile",
                        terminalNotEnough);
                }
                if (terminalPlan is HistoryTimelinePlanResult
                        .RecentReserveNotReached terminalReserve) {
                    return PrintSyncComplete(
                        expected,
                        committed,
                        "recent-reserve-not-reached",
                        audit is null ? "online" : "offline-reconcile",
                        terminalReserve);
                }
                if (terminalPlan is HistoryTimelinePlanResult
                        .LimitExceeded terminalLimit) {
                    return Print(
                        "timeline.sync",
                        "partition-limit",
                        terminalLimit,
                        2);
                }
                if (terminalPlan is HistoryTimelinePlanResult
                        .OfflineBootstrapRequired) {
                    if (audit is null) {
                        RecapGridCadenceOfflineAuditCaptureResult captured =
                            CaptureAudit(sealOperation);
                        if (captured is not
                                RecapGridCadenceOfflineAuditCaptureResult
                                .Available available) {
                            return Print(
                                "timeline.sync", "audit-limit", captured, 2);
                        }
                        audit = available.Audit;
                    }
                    return BuildOffline(
                        sealOperation,
                        audit,
                        expected,
                        committed,
                        maximumRows);
                }
                if (terminalPlan is HistoryTimelinePlanResult.Selected) {
                    return PrintRowLimit(expected, committed, maximumRows);
                }
                return Print(
                    "timeline.sync", "plan-failed", terminalPlan, 2);
            }
            finally {
                audit?.Dispose();
            }
            }
        }
    }

    private static int BuildOffline(
        RecapGridCadenceTimelineSealOperation seal,
        RecapGridCadenceOfflineAudit audit,
        TimelineHeadRef expected,
        int committed,
        int maximumRows
    ) {
        RecapGridCadenceOfflineBuilderOpenResult opened = seal
            .OpenOfflineBuilder(expected, audit);
        if (opened is not RecapGridCadenceOfflineBuilderOpenResult.Opened
                builder) {
            return Print(
                "timeline.sync", "offline-open-failed", opened, 2
            );
        }
        using RecapGridCadenceOfflineBuilder offline = builder.Builder;
        while (committed < maximumRows) {
            HistoryTimelineOfflineStepResult step = offline
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
            if (step is HistoryTimelineOfflineStepResult
                    .RecentReserveNotReached reserve) {
                return PrintSyncComplete(
                    expected,
                    committed,
                    "recent-reserve-not-reached",
                    "offline-build",
                    reserve);
            }
            if (step is HistoryTimelineOfflineStepResult
                    .RecentReserveProofUnavailable unavailable) {
                return Print(
                    "timeline.sync",
                    "recent-reserve-proof-unavailable",
                    unavailable,
                    2);
            }
            if (step is HistoryTimelineOfflineStepResult
                    .LimitExceeded limit) {
                return Print(
                    "timeline.sync",
                    "partition-limit",
                    limit,
                    2);
            }
            return Print("timeline.sync", "offline-step-failed", step, 2);
        }
        HistoryTimelineOfflineStepResult terminalProbe = offline
            .ProbeNextRow(expected);
        if (terminalProbe is HistoryTimelineOfflineStepResult.NotEnough
                terminalNotEnough) {
            return PrintSyncComplete(
                expected,
                committed,
                "not-enough",
                "offline-build",
                terminalNotEnough);
        }
        if (terminalProbe is HistoryTimelineOfflineStepResult
                .RecentReserveNotReached terminalReserve) {
            return PrintSyncComplete(
                expected,
                committed,
                "recent-reserve-not-reached",
                "offline-build",
                terminalReserve);
        }
        if (terminalProbe is HistoryTimelineOfflineStepResult
                .LimitExceeded terminalLimit) {
            return Print(
                "timeline.sync",
                "partition-limit",
                terminalLimit,
                2);
        }
        if (terminalProbe is HistoryTimelineOfflineStepResult.Selected) {
            return PrintRowLimit(expected, committed, maximumRows);
        }
        if (terminalProbe is HistoryTimelineOfflineStepResult
                .RecentReserveProofUnavailable terminalUnavailable) {
            return Print(
                "timeline.sync",
                "recent-reserve-proof-unavailable",
                terminalUnavailable,
                2);
        }
        return Print(
            "timeline.sync", "offline-step-failed", terminalProbe, 2);
    }

    private static int PrintRowLimit(
        TimelineHeadRef head,
        int committed,
        int maximumRows
    ) => Print(
        "timeline.sync",
        "row-limit",
        new { head, committed, maximumRows },
        2);

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

    private static RecapGridCadenceOfflineAuditCaptureResult CaptureAudit(
        RecapGridCadenceTimelineSealOperation seal
    ) {
        int maximumEvents = MaximumAuditEventsForTest.Value
            ?? MaximumRecapGridAuditEvents;
        if (maximumEvents is < 1 or > MaximumRecapGridAuditEvents) {
            throw new InvalidOperationException(
                "The audit test bound is outside the production cap."
            );
        }
        RecapGridCadenceOfflineAuditCaptureResult captured =
            seal.CaptureOfflineAudit(maximumEvents);
        BeforeAuditCompleteForTest.Value?.Invoke();
        return captured;
    }
}
