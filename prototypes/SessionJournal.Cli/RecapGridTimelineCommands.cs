using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    private static readonly O200kBaseHistoryUnitLoadEstimator
        RecapGridHistoryLoadEstimator = new();

    private static int Init(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "admission",
            "partition-algorithm", "history-load-estimator",
            "minimum-recent-history-load", "target-history-load",
            "max-raw-events", "max-rendered-bytes"
        );
        using SessionJournalEngine engine = OpenMutableBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridCadencePolicySpec cadencePolicy = ReadCadencePolicy(options);
        HistoryTimelineInitialPolicySpec initialPolicy =
            ToTimelineInitialPolicy(cadencePolicy);
        RecapGridControlAdmission admission = ReadAdmission(options);
        if ((admission.Permissions & RecapGridControlPermission.Create)
            != RecapGridControlPermission.Create) {
            throw new ArgumentException(
                "The admission does not authorize Control creation."
            );
        }

        RecapGridCadenceCreateResult cadence =
            RecapGridCadenceFactory.Create(engine, cadencePolicy);
        object cadenceStep = DescribeCadenceCreate(cadence);
        if (!IsCadenceCreated(cadence, cadencePolicy)) {
            return Print(
                "init",
                "cadence-failed",
                new { cadence = cadenceStep },
                2);
        }

        HistoryTimelineCreateResult timeline = HistoryTimelineFactory.Create(
            engine.ReadView,
            initialPolicy,
            RecapGridHistoryLoadEstimator
        );
        object timelineStep = DescribeTimelineCreate(timeline);
        if (!IsTimelineCreated(timeline)) {
            return Print(
                "init",
                "timeline-failed",
                new { timeline = timelineStep },
                2
            );
        }

        RecapGridControlCreateResult control = RecapGridControlFactory.Create(
            engine.ReadView.Path,
            engine.BranchRefId,
            admission
        );
        object controlStep = DescribeControlCreate(control);
        if (!IsControlCreated(control)) {
            return Print(
                "init",
                "control-failed",
                new { timeline = timelineStep, control = controlStep },
                2
            );
        }

        RecapGridStoreCreateResult store = RecapGridStoreFactory.Create(
            engine.ReadView.Path
        );
        object storeStep = DescribeStoreCreate(store);
        bool success = store is RecapGridStoreCreateResult.Created
            or RecapGridStoreCreateResult.AlreadyExists;
        return Print(
            "init",
            success ? "ready" : "store-failed",
            new {
                refId = engine.BranchRefId.ToHexString(),
                cadence = cadenceStep,
                timeline = timelineStep,
                control = controlStep,
                store = storeStep
            },
            success ? 0 : 2
        );
    }

    private static int TimelineCreate(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "partition-algorithm",
            "history-load-estimator", "minimum-recent-history-load",
            "target-history-load",
            "max-raw-events", "max-rendered-bytes"
        );
        using SessionJournalEngine engine = OpenMutableBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        RecapGridCadencePolicySpec cadencePolicy = ReadCadencePolicy(options);
        RecapGridCadenceCreateResult cadence =
            RecapGridCadenceFactory.Create(engine, cadencePolicy);
        if (!IsCadenceCreated(cadence, cadencePolicy)) {
            return Print(
                "timeline.create",
                "cadence-failed",
                DescribeCadenceCreate(cadence),
                2);
        }
        HistoryTimelineCreateResult result = HistoryTimelineFactory.Create(
            engine.ReadView,
            ToTimelineInitialPolicy(cadencePolicy),
            RecapGridHistoryLoadEstimator
        );
        bool success = IsTimelineCreated(result);
        return Print(
            "timeline.create",
            success ? "ready" : "failed",
            new {
                cadence = DescribeCadenceCreate(cadence),
                timeline = DescribeTimelineCreate(result)
            },
            success ? 0 : 2
        );
    }

    private static bool IsCadenceCreated(
        RecapGridCadenceCreateResult result,
        RecapGridCadencePolicySpec expected
    ) => result switch {
        RecapGridCadenceCreateResult.Created created
            => CadencePoliciesEqual(created.Snapshot.Policy, expected),
        RecapGridCadenceCreateResult.AlreadyExists existing
            => CadencePoliciesEqual(existing.Snapshot.Policy, expected),
        _ => false
    };

    private static bool CadencePoliciesEqual(
        RecapGridCadencePolicySpec left,
        RecapGridCadencePolicySpec right
    ) => left.MinimumRecentHistoryLoad
            == right.MinimumRecentHistoryLoad
        && string.Equals(left.PartitionAlgorithmId,
            right.PartitionAlgorithmId, StringComparison.Ordinal)
        && string.Equals(left.HistoryLoadEstimatorId,
            right.HistoryLoadEstimatorId, StringComparison.Ordinal)
        && left.TargetHistoryLoad == right.TargetHistoryLoad
        && left.MaxRawEvents == right.MaxRawEvents
        && left.MaxRenderedBytes == right.MaxRenderedBytes;

    private static object DescribeCadenceCreate(
        RecapGridCadenceCreateResult result
    ) => result switch {
        RecapGridCadenceCreateResult.Created value
            => new { status = "created", value.Snapshot.Head },
        RecapGridCadenceCreateResult.AlreadyExists value
            => new { status = "already-exists", value.Snapshot.Head },
        RecapGridCadenceCreateResult.Busy
            => new { status = "busy" },
        RecapGridCadenceCreateResult.CommitIndeterminate value
            => new { status = "commit-indeterminate", value.Intended,
                value.Observed },
        RecapGridCadenceCreateResult.UnsupportedSchema value
            => new { status = "unsupported-schema", value.Version },
        RecapGridCadenceCreateResult.PlatformUnsupported
            => new { status = "platform-unsupported" },
        RecapGridCadenceCreateResult.Invalid value
            => new { status = "invalid", value.Code, value.Detail },
        _ => new { status = "invalid-outcome" }
    };

    private static int TimelineInspect(CliOptions options, bool verify) {
        options.EnsureOnly("input", "branch");
        using SessionJournalEngine engine = OpenBranch(options);
        HistoryTimelineInspectResult result = verify
            ? HistoryTimelineMaintenance.Verify(
                engine.ReadView.Path,
                engine.BranchRefId
            )
            : HistoryTimelineMaintenance.Inspect(
                engine.ReadView.Path,
                engine.BranchRefId
            );
        return result switch {
            HistoryTimelineInspectResult.Available available => Print(
                verify ? "timeline.verify" : "timeline.inspect",
                "available",
                new {
                    available.Locator,
                    available.Head,
                    locatorCanonicalBase64 = Convert.ToBase64String(
                        available.Locator.ToCanonicalBytes()
                    ),
                    headCanonicalBase64 = Convert.ToBase64String(
                        available.Head.ToCanonicalBytes()
                    )
                }
            ),
            HistoryTimelineInspectResult.Absent => Print(
                verify ? "timeline.verify" : "timeline.inspect",
                "absent",
                exitCode: 2
            ),
            HistoryTimelineInspectResult.Busy => Print(
                verify ? "timeline.verify" : "timeline.inspect",
                "busy",
                exitCode: 2
            ),
            HistoryTimelineInspectResult.Invalid invalid => Print(
                verify ? "timeline.verify" : "timeline.inspect",
                "invalid",
                invalid,
                2
            ),
            _ => Print(
                verify ? "timeline.verify" : "timeline.inspect",
                "invalid",
                new { code = "TimelineInspectOutcomeInvalid" },
                2
            )
        };
    }

    private static int TimelineExport(CliOptions options) {
        options.EnsureOnly("input", "branch", "after", "max-rows");
        using SessionJournalEngine engine = OpenBranch(options);
        HistoryTimelinePathCursor? cursor = options.GetOptionalSingle("after")
            is { } value
            ? HistoryTimelinePathCursor.Parse(value)
            : null;
        int maximumRows = options.GetOptionalSingle("max-rows") is { } raw
            ? ParseBoundedInt(raw, 1, HistoryTimelineStoreLimits.MaximumPathPageRows,
                "--max-rows")
            : HistoryTimelineStoreLimits.MaximumPathPageRows;
        HistoryTimelineExportResult result = HistoryTimelineMaintenance.Export(
            engine.ReadView.Path,
            engine.BranchRefId,
            cursor: cursor,
            maximumRows: maximumRows
        );
        return result switch {
            HistoryTimelineExportResult.Page page => Print(
                "timeline.export",
                "page",
                new {
                    page.Value.Locator,
                    page.Value.Head,
                    locatorCanonicalBase64 = Convert.ToBase64String(
                        page.Value.Locator.ToCanonicalBytes()
                    ),
                    headCanonicalBase64 = Convert.ToBase64String(
                        page.Value.Head.ToCanonicalBytes()
                    ),
                    rows = page.Value.Path.Rows.Select(static row =>
                        row.Descriptor),
                    next = page.Value.Path.Next?.Value
                }
            ),
            HistoryTimelineExportResult.Absent => Print(
                "timeline.export", "absent", exitCode: 2),
            HistoryTimelineExportResult.Busy => Print(
                "timeline.export", "busy", exitCode: 2),
            HistoryTimelineExportResult.StaleTimelineHead stale => Print(
                "timeline.export", "stale-timeline-head", stale, 2),
            HistoryTimelineExportResult.Invalid invalid => Print(
                "timeline.export", "invalid", invalid, 2),
            _ => Print(
                "timeline.export",
                "invalid",
                new { code = "TimelineExportOutcomeInvalid" },
                2
            )
        };
    }

    private static int TimelineBackup(CliOptions options) {
        options.EnsureOnly("input", "branch", "confirm-ref", "output");
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        string destination = options.RequireSingle("output");
        CliIo.EnsurePathChainHasNoReparsePoint(destination, "--output");
        HistoryTimelineBackupResult result = HistoryTimelineMaintenance.Backup(
            engine.ReadView.Path,
            engine.BranchRefId,
            destination
        );
        return result switch {
            HistoryTimelineBackupResult.Created created => Print(
                "timeline.backup", "created", created),
            HistoryTimelineBackupResult.Absent => Print(
                "timeline.backup", "absent", exitCode: 2),
            HistoryTimelineBackupResult.Busy => Print(
                "timeline.backup", "busy", exitCode: 2),
            HistoryTimelineBackupResult.LimitExceeded limit => Print(
                "timeline.backup", "limit", limit, 2),
            HistoryTimelineBackupResult.Invalid invalid => Print(
                "timeline.backup", "invalid", invalid, 2),
            _ => Print("timeline.backup", "invalid-outcome", exitCode: 2)
        };
    }

    private static int TimelineRestore(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "confirm-locator",
            "confirm-head", "backup"
        );
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        ActiveTimelineLocator locator =
            HistoryTimelineCanonicalCodec.DecodeActiveTimelineLocator(
                ReadBoundedFile(
                    options.RequireSingle("confirm-locator"),
                    MaximumInputUtf8Bytes
                )
            );
        TimelineHeadRef head = HistoryTimelineCanonicalCodec.DecodeTimelineHead(
            ReadBoundedFile(
                options.RequireSingle("confirm-head"),
                MaximumInputUtf8Bytes
            )
        );
        if (locator.RefId != engine.BranchRefId
            || head.RefId != engine.BranchRefId) {
            throw new ArgumentException(
                "Timeline confirmation scope differs from --confirm-ref."
            );
        }
        HistoryTimelineRestoreResult result = HistoryTimelineMaintenance.Restore(
            engine.ReadView.Path,
            engine.BranchRefId,
            new HistoryTimelineActiveConfirmation(locator, head),
            options.RequireSingle("backup")
        );
        return result switch {
            HistoryTimelineRestoreResult.Restored restored => Print(
                "timeline.restore", "restored", restored),
            HistoryTimelineRestoreResult.ConfirmationMismatch mismatch => Print(
                "timeline.restore", "stale-confirmation", mismatch, 2),
            HistoryTimelineRestoreResult.Busy => Print(
                "timeline.restore", "busy", exitCode: 2),
            HistoryTimelineRestoreResult.LimitExceeded limit => Print(
                "timeline.restore", "limit", limit, 2),
            HistoryTimelineRestoreResult.Invalid invalid => Print(
                "timeline.restore", "invalid", invalid, 2),
            _ => Print("timeline.restore", "invalid-outcome", exitCode: 2)
        };
    }

    private static int TimelineAbandon(CliOptions options) {
        options.EnsureOnly(
            "input", "branch", "confirm-ref", "confirm-locator",
            "partition-algorithm", "history-load-estimator",
            "target-history-load", "max-raw-events", "max-rendered-bytes"
        );
        using SessionJournalEngine engine = OpenBranch(options);
        RequireConfirmedRef(options, engine.BranchRefId);
        ActiveTimelineLocator locator =
            HistoryTimelineCanonicalCodec.DecodeActiveTimelineLocator(
                ReadBoundedFile(
                    options.RequireSingle("confirm-locator"),
                    MaximumInputUtf8Bytes
                )
            );
        if (locator.RefId != engine.BranchRefId) {
            throw new ArgumentException(
                "Timeline locator confirmation differs from --confirm-ref."
            );
        }
        HistoryTimelineAbandonResult result =
            HistoryTimelineMaintenance.Abandon(
                engine.ReadView.Path,
                engine.BranchRefId,
                locator,
                ReadInitialPolicy(options),
                RecapGridHistoryLoadEstimator
            );
        return result switch {
            HistoryTimelineAbandonResult.Abandoned abandoned => Print(
                "timeline.abandon", "abandoned", abandoned),
            HistoryTimelineAbandonResult.ConfirmationMismatch mismatch => Print(
                "timeline.abandon", "stale-confirmation", mismatch, 2),
            HistoryTimelineAbandonResult.Busy => Print(
                "timeline.abandon", "busy", exitCode: 2),
            HistoryTimelineAbandonResult.Invalid invalid => Print(
                "timeline.abandon", "invalid", invalid, 2),
            _ => Print("timeline.abandon", "invalid-outcome", exitCode: 2)
        };
    }

    private static bool IsTimelineCreated(HistoryTimelineCreateResult result)
        => result is HistoryTimelineCreateResult.Created
            or HistoryTimelineCreateResult.AlreadyExists;

    private static object DescribeTimelineCreate(
        HistoryTimelineCreateResult result
    ) => result switch {
        HistoryTimelineCreateResult.Created created => new {
            status = "created", created.Locator, created.InitialHead
        },
        HistoryTimelineCreateResult.AlreadyExists existing => new {
            status = "already-exists", existing.Locator
        },
        HistoryTimelineCreateResult.Busy => new { status = "busy" },
        HistoryTimelineCreateResult.LimitExceeded limit => new {
            status = "limit-exceeded", limit.Limit
        },
        HistoryTimelineCreateResult.Invalid invalid => new {
            status = "invalid", invalid.Code, invalid.Detail
        },
        _ => new { status = "invalid-outcome" }
    };

    private static bool IsControlCreated(RecapGridControlCreateResult result)
        => result is RecapGridControlCreateResult.Created
            or RecapGridControlCreateResult.AlreadyExists;

    private static object DescribeControlCreate(
        RecapGridControlCreateResult result
    ) => result switch {
        RecapGridControlCreateResult.Created created => new {
            status = "created", created.Head
        },
        RecapGridControlCreateResult.AlreadyExists => new {
            status = "already-exists"
        },
        RecapGridControlCreateResult.CommitIndeterminate uncertain => new {
            status = "commit-indeterminate",
            uncertain.Intended,
            uncertain.Observed,
            nextAction = "inspect"
        },
        RecapGridControlCreateResult.TimelineAbsent => new {
            status = "timeline-absent"
        },
        RecapGridControlCreateResult.TimelineUnsupportedSchema schema => new {
            status = "timeline-unsupported-schema", schema.SchemaVersion
        },
        RecapGridControlCreateResult.ControlUnsupportedSchema schema => new {
            status = "control-unsupported-schema", schema.SchemaVersion
        },
        RecapGridControlCreateResult.Unauthorized unauthorized => new {
            status = "unauthorized", unauthorized.Rule
        },
        RecapGridControlCreateResult.Busy => new { status = "busy" },
        RecapGridControlCreateResult.LimitExceeded limit => new {
            status = "limit-exceeded", limit.Limit
        },
        RecapGridControlCreateResult.Invalid invalid => new {
            status = "invalid", invalid.Code, invalid.Detail
        },
        _ => new { status = "invalid-outcome" }
    };

    private static object DescribeStoreCreate(RecapGridStoreCreateResult result)
        => result switch {
            RecapGridStoreCreateResult.Created created => new {
                status = "created", created.Identity
            },
            RecapGridStoreCreateResult.AlreadyExists => new {
                status = "already-exists"
            },
            RecapGridStoreCreateResult.Busy => new { status = "busy" },
            RecapGridStoreCreateResult.Limit limit => new {
                status = "limit-exceeded", limit.Name
            },
            RecapGridStoreCreateResult.CommitIndeterminate uncertain => new {
                status = "commit-indeterminate",
                uncertain.Intended,
                uncertain.Observed,
                nextAction = "inspect"
            },
            RecapGridStoreCreateResult.PlatformUnsupported => new {
                status = "platform-unsupported"
            },
            RecapGridStoreCreateResult.Invalid invalid => new {
                status = "invalid", invalid.Code, invalid.Detail
            },
            _ => new { status = "invalid-outcome" }
        };

    private static int ParseBoundedInt(
        string value,
        int minimum,
        int maximum,
        string option
    ) => int.TryParse(value, out int parsed)
        && parsed >= minimum
        && parsed <= maximum
            ? parsed
            : throw new ArgumentException(
                $"{option} must be between {minimum} and {maximum}."
            );
}
