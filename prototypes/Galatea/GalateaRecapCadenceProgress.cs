using System.Globalization;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;

namespace Atelia.Galatea.Server;

/// <summary>
/// Galatea-local, mutation-free projection of the durable Recap cadence and
/// the exact recent raw-history suffix. This does not capture through the
/// Timeline coordinator or open any Recap build lifecycle.
/// </summary>
internal static class GalateaRecapCadenceProgress {
    internal const string ExactFreshness = "exact";
    internal const string StaleFreshness = "stale";

    internal static readonly AsyncLocal<Action?>
        BeforeFinalAuthorityFenceForTest = new();

    internal static RecapCadenceProgressSnapshotDto Inspect(
        SessionJournalReadView selectedRef,
        EventAddress capturedRawHead,
        IHistoryUnitLoadEstimator estimator,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(selectedRef);
        ArgumentNullException.ThrowIfNull(estimator);
        cancellationToken.ThrowIfCancellationRequested();

        try {
            RawHeadFenceResult initialRawFence = ObserveRawHead(
                selectedRef,
                capturedRawHead);
            if (initialRawFence is not RawHeadFenceResult.Exact) {
                return MapRawHeadFence(
                    capturedRawHead,
                    initialRawFence,
                    Unavailable(
                        capturedRawHead,
                        "raw-head-unavailable"));
            }

            RecapGridCadenceReaderOpenResult cadenceOpened =
                RecapGridCadenceFactory.OpenReader(selectedRef);
            if (cadenceOpened is not RecapGridCadenceReaderOpenResult
                    .Opened cadenceAvailable) {
                return FinishWithRawFence(
                    selectedRef,
                    capturedRawHead,
                    MapCadenceOpen(cadenceOpened, capturedRawHead));
            }

            using RecapGridCadenceReaderHandle cadence =
                cadenceAvailable.Handle;
            RecapGridCadenceReadResult cadenceRead =
                cadence.Reader.ReadSnapshot();
            if (cadenceRead is not RecapGridCadenceReadResult
                    .Available cadenceSnapshotAvailable) {
                return FinishWithRawFence(
                    selectedRef,
                    capturedRawHead,
                    MapCadenceRead(cadenceRead, capturedRawHead));
            }
            RecapGridCadenceSnapshot cadenceSnapshot =
                cadenceSnapshotAvailable.Snapshot;
            RecapGridCadencePolicySpec cadencePolicy =
                cadenceSnapshot.Policy;
            RecapCadenceProgressSnapshotDto cadenceBase = WithCadence(
                capturedRawHead,
                cadencePolicy);

            if (cadenceSnapshot.Head.RefId != selectedRef.BranchRefId) {
                return FinishWithRawFence(
                    selectedRef,
                    capturedRawHead,
                    cadenceBase with {
                        State = "unavailable",
                        Code = "cadence-ref-mismatch",
                        Detail = "Cadence belongs to another SessionJournal Ref."
                    });
            }
            if (!string.Equals(
                    cadencePolicy.HistoryLoadEstimatorId,
                    estimator.Id,
                    StringComparison.Ordinal)) {
                return FinishWithRawFence(
                    selectedRef,
                    capturedRawHead,
                    cadenceBase with {
                        State = "unavailable",
                        Code = "history-load-estimator-unavailable",
                        Detail = cadencePolicy.HistoryLoadEstimatorId
                    });
            }

            HistoryTimelineBuildReadSessionOpenResult timelineOpened =
                HistoryTimelineFactory.OpenBuildReadSession(
                    selectedRef,
                    estimator);
            if (timelineOpened is not
                HistoryTimelineBuildReadSessionOpenResult
                    .Opened timelineAvailable) {
                return FinishWithCadenceAndRawFences(
                    selectedRef,
                    capturedRawHead,
                    cadence,
                    cadenceSnapshot,
                    MapTimelineOpen(
                        timelineOpened,
                        cadenceBase));
            }

            using HistoryTimelineBuildReadSession timeline =
                timelineAvailable.Session;
            HistoryTimelineSnapshotResult timelineRead =
                timeline.Reader.ReadSnapshot();
            if (timelineRead is not HistoryTimelineSnapshotResult
                    .Available timelineSnapshotAvailable) {
                return FinishWithAllFences(
                    selectedRef,
                    capturedRawHead,
                    cadence,
                    cadenceSnapshot,
                    timeline,
                    expectedTimelineHead: null,
                    MapTimelineRead(timelineRead, cadenceBase));
            }
            TimelineHeadRef timelineHead = timelineSnapshotAvailable.Head;
            PartitionPolicyRevision expectedPolicy =
                PartitionPolicyRevision.Create(
                    timelineHead.TimelineId,
                    cadencePolicy.PartitionAlgorithmId,
                    cadencePolicy.HistoryLoadEstimatorId,
                    new HistoryLoadUnit(cadencePolicy.TargetHistoryLoad),
                    cadencePolicy.MaxRawEvents,
                    cadencePolicy.MaxRenderedBytes);

            RecapCadenceProgressSnapshotDto? authorityFailure =
                ValidateTimelineAuthority(
                    selectedRef,
                    timeline,
                    timelineHead,
                    expectedPolicy,
                    cadenceBase);
            if (authorityFailure is not null) {
                return FinishWithAllFences(
                    selectedRef,
                    capturedRawHead,
                    cadence,
                    cadenceSnapshot,
                    timeline,
                    timelineHead,
                    authorityFailure);
            }

            BaselineSeedResult seedRead = ReadBaselineSeed(
                selectedRef,
                capturedRawHead,
                timeline,
                timelineHead,
                cadencePolicy.MaxRawEvents,
                cancellationToken);
            if (seedRead is not BaselineSeedResult.Available seedAvailable) {
                return FinishWithAllFences(
                    selectedRef,
                    capturedRawHead,
                    cadence,
                    cadenceSnapshot,
                    timeline,
                    timelineHead,
                    MapSeedRead(seedRead, cadenceBase));
            }
            SessionHistoryPlanningSeed seed = seedAvailable.Seed;

            SessionHistoryPlanningWindowReadResult windowRead =
                selectedRef.ReadHistoryPlanningWindowAtBounded(
                    capturedRawHead,
                    seed,
                    cadencePolicy.MaxRawEvents,
                    cancellationToken);
            if (windowRead is not SessionHistoryPlanningWindowReadResult
                    .Available windowAvailable) {
                return FinishWithAllFences(
                    selectedRef,
                    capturedRawHead,
                    cadence,
                    cadenceSnapshot,
                    timeline,
                    timelineHead,
                    MapWindowRead(windowRead, cadenceBase, seed.Address));
            }

            SessionHistoryPlanningWindow window = windowAvailable.Window;
            cancellationToken.ThrowIfCancellationRequested();
            HistoryLoadProjection current = HistoryLoadProjector.Measure(
                window,
                seed.Address,
                estimator);
            cancellationToken.ThrowIfCancellationRequested();
            HistoryLoadBaseline baseline = HistoryLoadBaselineResolver.Resolve(
                window.StartExclusive,
                window.Units.Count,
                window.ReplaySafeBoundaries,
                seed.Address);
            HistoryPartitionResult partition = HistoryPartitioner.Partition(
                window,
                baseline,
                expectedPolicy,
                estimator);
            cancellationToken.ThrowIfCancellationRequested();

            RecapCadenceProgressSnapshotDto projected = Project(
                cadenceBase,
                seed.Address,
                window,
                current,
                partition,
                cadencePolicy,
                estimator,
                cancellationToken);
            return FinishWithAllFences(
                selectedRef,
                capturedRawHead,
                cadence,
                cadenceSnapshot,
                timeline,
                timelineHead,
                projected);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return FinishFailureWithRawFence(
                selectedRef,
                capturedRawHead,
                exception);
        }
    }

    private static RecapCadenceProgressSnapshotDto Project(
        RecapCadenceProgressSnapshotDto cadenceBase,
        EventAddress baselineAddress,
        SessionHistoryPlanningWindow window,
        HistoryLoadProjection current,
        HistoryPartitionResult partition,
        RecapGridCadencePolicySpec cadencePolicy,
        IHistoryUnitLoadEstimator estimator,
        CancellationToken cancellationToken
    ) {
        long currentLoad = current.Growth.Value;
        int recentUnitCount = checked(
            window.Units.Count - current.BaselineCompletedUnitCount);
        long idealThreshold = checked(
            cadencePolicy.TargetHistoryLoad
            + cadencePolicy.MinimumRecentHistoryLoad);
        RecapCadenceProgressSnapshotDto measured = cadenceBase with {
            CadenceBaseline = Address(baselineAddress),
            RecentHistoryPlanningUnitCount = recentUnitCount,
            RecentHistoryLoad = Decimal(currentLoad),
            BuildThresholdHistoryLoad = Decimal(idealThreshold),
            RemainingHistoryLoad = Decimal(Remaining(
                idealThreshold,
                currentLoad))
        };

        switch (partition) {
            case HistoryPartitionResult.LimitExceeded limit:
                return measured with {
                    State = "limited",
                    BuildThresholdHistoryLoad = null,
                    RemainingHistoryLoad = null,
                    Code = limit.Limit switch {
                        HistoryPartitionLimitKind.MaxRawEvents
                            => "max-raw-events",
                        HistoryPartitionLimitKind.MaxRenderedBytes
                            => "max-rendered-bytes",
                        _ => "partition-limit"
                    },
                    Detail = limit.Limit.ToString()
                };

            case HistoryPartitionResult.NotEnough:
                return measured with {
                    State = currentLoad
                        < cadencePolicy.TargetHistoryLoad
                            ? "below-target"
                            : "awaiting-replay-safe-boundary"
                };

            case HistoryPartitionResult.Selected selected:
                cancellationToken.ThrowIfCancellationRequested();
                HistoryLoadProjection retained =
                    HistoryLoadProjector.Measure(
                        window,
                        selected.Point.EndInclusive,
                        estimator);
                cancellationToken.ThrowIfCancellationRequested();
                long actualThreshold = checked(
                    selected.Point.MeasuredHistoryLoad.Value
                    + cadencePolicy.MinimumRecentHistoryLoad);
                long retainedLoad = retained.Growth.Value;
                return measured with {
                    State = retainedLoad
                        < cadencePolicy.MinimumRecentHistoryLoad
                            ? "awaiting-recent-reserve"
                            : "cadence-ready",
                    BuildThresholdHistoryLoad = Decimal(actualThreshold),
                    RemainingHistoryLoad = Decimal(Remaining(
                        actualThreshold,
                        currentLoad)),
                    Code = retainedLoad
                        < cadencePolicy.MinimumRecentHistoryLoad
                            ? "recent-reserve-short"
                            : null,
                    Detail = retainedLoad
                        < cadencePolicy.MinimumRecentHistoryLoad
                            ? $"retained={Decimal(retainedLoad)}"
                            : null
                };

            default:
                return measured with {
                    State = "unavailable",
                    Code = "partition-outcome-unknown"
                };
        }
    }

    private static BaselineSeedResult ReadBaselineSeed(
        SessionJournalReadView selectedRef,
        EventAddress capturedRawHead,
        HistoryTimelineBuildReadSession timeline,
        TimelineHeadRef timelineHead,
        int maximumRawEvents,
        CancellationToken cancellationToken
    ) {
        if (timelineHead.HeadRowId is null) {
            SessionCreatedPlanningSeedReadResult result =
                selectedRef.ReadSessionCreatedPlanningSeedAtBounded(
                    capturedRawHead,
                    maximumRawEvents,
                    cancellationToken);
            return result switch {
                SessionCreatedPlanningSeedReadResult.Available available
                    => new BaselineSeedResult.Available(available.Seed),
                SessionCreatedPlanningSeedReadResult.BeyondPrefix
                    => new BaselineSeedResult.Limited(
                        "session-created-beyond-prefix"),
                _ => new BaselineSeedResult.Failure(
                    "planning-seed-outcome-unknown")
            };
        }
        HistoryTimelineReaderRowResult row =
            timeline.Reader.ReadSelectedRow(
                timelineHead,
                timelineHead.HeadRowId.Value);
        if (row is not HistoryTimelineReaderRowResult.Selected selected) {
            return new BaselineSeedResult.RowFailure(row);
        }
        SessionHistoryPlanningSeed seed =
            selectedRef.CreateHistoryPlanningSeed(
                selected.Row.Descriptor.EndInclusive,
                selected.Row.Descriptor.EndSetups,
                cancellationToken);
        return new BaselineSeedResult.Available(seed);
    }

    private static RecapCadenceProgressSnapshotDto?
        ValidateTimelineAuthority(
        SessionJournalReadView selectedRef,
        HistoryTimelineBuildReadSession timeline,
        TimelineHeadRef timelineHead,
        PartitionPolicyRevision expectedPolicy,
        RecapCadenceProgressSnapshotDto cadenceBase
    ) {
        if (timelineHead.RefId != selectedRef.BranchRefId
            || timeline.Locator.RefId != selectedRef.BranchRefId
            || timeline.Locator.ActiveTimelineId != timelineHead.TimelineId) {
            return cadenceBase with {
                State = "unavailable",
                Code = "timeline-ref-mismatch",
                Detail = "Timeline belongs to another SessionJournal Ref."
            };
        }
        if (!string.Equals(
                timelineHead.ActivePartitionPolicyDigest,
                expectedPolicy.PolicyDigest,
                StringComparison.Ordinal)) {
            return cadenceBase with {
                State = "unavailable",
                Code = "cadence-timeline-policy-mismatch",
                Detail = timelineHead.ActivePartitionPolicyDigest
            };
        }
        return null;
    }

    private static RecapCadenceProgressSnapshotDto FinishWithAllFences(
        SessionJournalReadView selectedRef,
        EventAddress capturedRawHead,
        RecapGridCadenceReaderHandle cadence,
        RecapGridCadenceSnapshot expectedCadence,
        HistoryTimelineBuildReadSession timeline,
        TimelineHeadRef? expectedTimelineHead,
        RecapCadenceProgressSnapshotDto result
    ) {
        BeforeFinalAuthorityFenceForTest.Value?.Invoke();
        RawHeadFenceResult rawFence = ObserveRawHead(
            selectedRef,
            capturedRawHead);
        if (rawFence is not RawHeadFenceResult.Exact) {
            return MapRawHeadFence(
                capturedRawHead,
                rawFence,
                result);
        }
        if (expectedTimelineHead is not null) {
            HistoryTimelineSnapshotResult timelineAfter =
                timeline.Reader.ReadSnapshot();
            if (timelineAfter is not HistoryTimelineSnapshotResult
                    .Available available
                || available.Head != expectedTimelineHead) {
                return Stale(
                    capturedRawHead,
                    "timeline-head-changed");
            }
        }
        return RequireCadenceFence(
            capturedRawHead,
            cadence,
            expectedCadence,
            result);
    }

    private static RecapCadenceProgressSnapshotDto
        FinishWithCadenceAndRawFences(
        SessionJournalReadView selectedRef,
        EventAddress capturedRawHead,
        RecapGridCadenceReaderHandle cadence,
        RecapGridCadenceSnapshot expectedCadence,
        RecapCadenceProgressSnapshotDto result
    ) {
        BeforeFinalAuthorityFenceForTest.Value?.Invoke();
        RawHeadFenceResult rawFence = ObserveRawHead(
            selectedRef,
            capturedRawHead);
        if (rawFence is not RawHeadFenceResult.Exact) {
            return MapRawHeadFence(
                capturedRawHead,
                rawFence,
                result);
        }
        return RequireCadenceFence(
            capturedRawHead,
            cadence,
            expectedCadence,
            result);
    }

    private static RecapCadenceProgressSnapshotDto RequireCadenceFence(
        EventAddress capturedRawHead,
        RecapGridCadenceReaderHandle cadence,
        RecapGridCadenceSnapshot expectedCadence,
        RecapCadenceProgressSnapshotDto result
    ) {
        RecapGridCadenceReadResult cadenceAfter =
            cadence.Reader.ReadSnapshot();
        return cadenceAfter is RecapGridCadenceReadResult.Available available
            && available.Snapshot.Head == expectedCadence.Head
                ? result
                : Stale(capturedRawHead, "cadence-head-changed");
    }

    private static RecapCadenceProgressSnapshotDto FinishWithRawFence(
        SessionJournalReadView selectedRef,
        EventAddress capturedRawHead,
        RecapCadenceProgressSnapshotDto result
    ) {
        BeforeFinalAuthorityFenceForTest.Value?.Invoke();
        return MapRawHeadFence(
            capturedRawHead,
            ObserveRawHead(selectedRef, capturedRawHead),
            result);
    }

    private static RecapCadenceProgressSnapshotDto
        FinishFailureWithRawFence(
        SessionJournalReadView selectedRef,
        EventAddress capturedRawHead,
        Exception exception
    ) {
        RecapCadenceProgressSnapshotDto failure = new(
            ExactFreshness,
            "unavailable",
            Address(capturedRawHead),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            exception is HistoryLoadMeasurementException measurement
                ? measurement.Code
                : "inspection-failed",
            exception.Message);
        return MapRawHeadFence(
            capturedRawHead,
            ObserveRawHead(selectedRef, capturedRawHead),
            failure);
    }

    private static RecapCadenceProgressSnapshotDto MapCadenceOpen(
        RecapGridCadenceReaderOpenResult result,
        EventAddress capturedRawHead
    ) => result switch {
        RecapGridCadenceReaderOpenResult.Absent
            => Unprovisioned(capturedRawHead, "cadence-absent"),
        RecapGridCadenceReaderOpenResult.Busy
            => Unavailable(capturedRawHead, "cadence-busy"),
        RecapGridCadenceReaderOpenResult.UnsupportedSchema schema
            => Unavailable(
                capturedRawHead,
                $"cadence-schema-{schema.Version}"),
        RecapGridCadenceReaderOpenResult.PlatformUnsupported
            => Unavailable(
                capturedRawHead,
                "cadence-platform-unsupported"),
        RecapGridCadenceReaderOpenResult.Invalid invalid
            => Unavailable(
                capturedRawHead,
                $"cadence:{invalid.Code}",
                invalid.Detail),
        _ => Unavailable(capturedRawHead, "cadence-open-outcome-unknown")
    };

    private static RecapCadenceProgressSnapshotDto MapCadenceRead(
        RecapGridCadenceReadResult result,
        EventAddress capturedRawHead
    ) => result switch {
        RecapGridCadenceReadResult.Busy
            => Unavailable(capturedRawHead, "cadence-busy"),
        RecapGridCadenceReadResult.Disposed
            => Unavailable(capturedRawHead, "cadence-disposed"),
        RecapGridCadenceReadResult.UnsupportedSchema schema
            => Unavailable(
                capturedRawHead,
                $"cadence-schema-{schema.Version}"),
        RecapGridCadenceReadResult.Invalid invalid
            => Unavailable(
                capturedRawHead,
                $"cadence:{invalid.Code}",
                invalid.Detail),
        _ => Unavailable(capturedRawHead, "cadence-read-outcome-unknown")
    };

    private static RecapCadenceProgressSnapshotDto MapTimelineOpen(
        HistoryTimelineBuildReadSessionOpenResult result,
        RecapCadenceProgressSnapshotDto cadenceBase
    ) => result switch {
        HistoryTimelineBuildReadSessionOpenResult.Absent
            => cadenceBase with {
                State = "unprovisioned",
                Code = "timeline-absent"
            },
        HistoryTimelineBuildReadSessionOpenResult.Busy
            => cadenceBase with {
                State = "unavailable",
                Code = "timeline-busy"
            },
        HistoryTimelineBuildReadSessionOpenResult.UnsupportedSchema schema
            => cadenceBase with {
                State = "unavailable",
                Code = $"timeline-schema-{schema.SchemaVersion}"
            },
        HistoryTimelineBuildReadSessionOpenResult.Invalid invalid
            => cadenceBase with {
                State = "unavailable",
                Code = $"timeline:{invalid.Code}",
                Detail = invalid.Detail
            },
        _ => cadenceBase with {
            State = "unavailable",
            Code = "timeline-open-outcome-unknown"
        }
    };

    private static RecapCadenceProgressSnapshotDto MapTimelineRead(
        HistoryTimelineSnapshotResult result,
        RecapCadenceProgressSnapshotDto cadenceBase
    ) => result switch {
        HistoryTimelineSnapshotResult.Busy
            => cadenceBase with {
                State = "unavailable",
                Code = "timeline-busy"
            },
        HistoryTimelineSnapshotResult.UnsupportedSchema schema
            => cadenceBase with {
                State = "unavailable",
                Code = $"timeline-schema-{schema.SchemaVersion}"
            },
        HistoryTimelineSnapshotResult.Invalid invalid
            => cadenceBase with {
                State = "unavailable",
                Code = $"timeline:{invalid.Code}",
                Detail = invalid.Detail
            },
        _ => cadenceBase with {
            State = "unavailable",
            Code = "timeline-read-outcome-unknown"
        }
    };

    private static RecapCadenceProgressSnapshotDto MapSeedRead(
        BaselineSeedResult result,
        RecapCadenceProgressSnapshotDto cadenceBase
    ) {
        if (result is BaselineSeedResult.Limited limited) {
            return cadenceBase with {
                State = "limited",
                BuildThresholdHistoryLoad = null,
                RemainingHistoryLoad = null,
                Code = limited.Code
            };
        }
        if (result is BaselineSeedResult.RowFailure failure) {
            return failure.RowResult switch {
                HistoryTimelineReaderRowResult.StaleTimelineHead
                    => cadenceBase with {
                        Freshness = StaleFreshness,
                        State = "stale",
                        Code = "timeline-head-changed"
                    },
                HistoryTimelineReaderRowResult.Busy
                    => cadenceBase with {
                        State = "unavailable",
                        Code = "timeline-busy"
                    },
                HistoryTimelineReaderRowResult.NotOnSelectedPath missing
                    => cadenceBase with {
                        State = "unavailable",
                        Code = "timeline-head-row-not-selected",
                        Detail = missing.RowId.Value
                    },
                HistoryTimelineReaderRowResult.Invalid invalid
                    => cadenceBase with {
                        State = "unavailable",
                        Code = $"timeline:{invalid.Code}",
                        Detail = invalid.Detail
                    },
                _ => cadenceBase with {
                    State = "unavailable",
                    Code = "timeline-row-outcome-unknown"
                }
            };
        }
        if (result is BaselineSeedResult.Failure failureResult) {
            return cadenceBase with {
                State = "unavailable",
                Code = failureResult.Code,
                Detail = failureResult.Detail
            };
        }
        return cadenceBase with {
            State = "unavailable",
            Code = "planning-seed-outcome-unknown"
        };
    }

    private static RecapCadenceProgressSnapshotDto MapWindowRead(
        SessionHistoryPlanningWindowReadResult result,
        RecapCadenceProgressSnapshotDto cadenceBase,
        EventAddress baseline
    ) => result switch {
        SessionHistoryPlanningWindowReadResult.BeyondPrefix
            => cadenceBase with {
                State = "limited",
                CadenceBaseline = Address(baseline),
                BuildThresholdHistoryLoad = null,
                RemainingHistoryLoad = null,
                Code = "recent-history-beyond-prefix"
            },
        _ => cadenceBase with {
            State = "unavailable",
            Code = "planning-window-outcome-unknown"
        }
    };

    private static RecapCadenceProgressSnapshotDto WithCadence(
        EventAddress capturedRawHead,
        RecapGridCadencePolicySpec policy
    ) {
        long idealThreshold = checked(
            policy.TargetHistoryLoad
            + policy.MinimumRecentHistoryLoad);
        return new RecapCadenceProgressSnapshotDto(
            ExactFreshness,
            "unavailable",
            Address(capturedRawHead),
            null,
            null,
            null,
            Decimal(policy.TargetHistoryLoad),
            Decimal(policy.MinimumRecentHistoryLoad),
            Decimal(idealThreshold),
            null,
            policy.HistoryLoadEstimatorId);
    }

    private static RecapCadenceProgressSnapshotDto Unprovisioned(
        EventAddress capturedRawHead,
        string code
    ) => new(
        ExactFreshness,
        "unprovisioned",
        Address(capturedRawHead),
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        code);

    private static RecapCadenceProgressSnapshotDto Unavailable(
        EventAddress capturedRawHead,
        string code,
        string? detail = null
    ) => new(
        ExactFreshness,
        "unavailable",
        Address(capturedRawHead),
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        code,
        detail);

    private static RecapCadenceProgressSnapshotDto Stale(
        EventAddress capturedRawHead,
        string code = "raw-head-changed"
    ) => new(
        StaleFreshness,
        "stale",
        Address(capturedRawHead),
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        code);

    private static RawHeadFenceResult ObserveRawHead(
        SessionJournalReadView selectedRef,
        EventAddress capturedRawHead
    ) {
        try {
            EventAddress? observed = selectedRef.ReadCurrentHead();
            return observed == capturedRawHead
                ? new RawHeadFenceResult.Exact()
                : new RawHeadFenceResult.Changed(observed);
        }
        catch (OperationCanceledException) {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return new RawHeadFenceResult.Unavailable(
                exception.GetType().Name,
                exception.Message);
        }
    }

    private static RecapCadenceProgressSnapshotDto MapRawHeadFence(
        EventAddress capturedRawHead,
        RawHeadFenceResult fence,
        RecapCadenceProgressSnapshotDto exactResult
    ) => fence switch {
        RawHeadFenceResult.Exact => exactResult,
        RawHeadFenceResult.Changed
            => Stale(capturedRawHead),
        RawHeadFenceResult.Unavailable unavailable
            => exactResult with {
                Freshness = StaleFreshness,
                State = "unavailable",
                Code = "raw-head-observation-failed",
                Detail = $"{unavailable.Code}: {unavailable.Detail}"
            },
        _ => exactResult with {
            Freshness = StaleFreshness,
            State = "unavailable",
            Code = "raw-head-observation-outcome-unknown"
        }
    };

    private static long Remaining(long threshold, long current)
        => current >= threshold ? 0 : checked(threshold - current);

    private static string Decimal(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static string Address(EventAddress value)
        => EventAddressTextCodec.Format(value);

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    private abstract record BaselineSeedResult {
        private BaselineSeedResult() { }

        internal sealed record Available(SessionHistoryPlanningSeed Seed)
            : BaselineSeedResult;

        internal sealed record Limited(string Code)
            : BaselineSeedResult;

        internal sealed record RowFailure(
            HistoryTimelineReaderRowResult RowResult)
            : BaselineSeedResult;

        internal sealed record Failure(string Code, string? Detail = null)
            : BaselineSeedResult;
    }

    private abstract record RawHeadFenceResult {
        private RawHeadFenceResult() { }

        internal sealed record Exact : RawHeadFenceResult;

        internal sealed record Changed(EventAddress? Observed)
            : RawHeadFenceResult;

        internal sealed record Unavailable(string Code, string Detail)
            : RawHeadFenceResult;
    }
}
