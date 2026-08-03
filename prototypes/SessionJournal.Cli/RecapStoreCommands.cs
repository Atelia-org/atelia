using Atelia.EventJournal;
using Atelia.Completion;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapStoreCommands {
    private const string OperationSchema =
        "atelia.session-journal.derived-recap-store-operation.v1";
    private const string InspectionSchema =
        "atelia.session-journal.derived-recap-store-inspection.v2";

    internal static Task<int> RunAsync(
        string[] args,
        ICompletionClientFactory completionClientFactory
    ) {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        if (args.Length == 0
            || args[0] is "-h" or "--help"
            || args[0].StartsWith("--", StringComparison.Ordinal)) {
            throw new ArgumentException(
                "recap requires one subcommand: planner-config, "
                + "history-load, "
                + "create, inspect, materialize-inspect, "
                + "run, resume, restore, abandon-building, or reset."
            );
        }

        string subcommand = args[0];
        if (string.Equals(
                subcommand,
                "planner-config",
                StringComparison.Ordinal
            )) {
            return RecapPlannerConfigCommands.RunAsync(
                args.Skip(1).ToArray()
            );
        }
        if (string.Equals(
                subcommand,
                "history-load",
                StringComparison.Ordinal
            )) {
            return RecapHistoryLoadCommands.RunAsync(
                args.Skip(1).ToArray()
            );
        }
        CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
        return subcommand switch {
            "create" => CreateAsync(options),
            "inspect" => InspectAsync(options),
            "materialize-inspect" =>
                RecapMaterializationInspectionCommands.RunAsync(
                    options
                ),
            "run" => RecapExecutionCommands.RunAsync(
                options,
                completionClientFactory
            ),
            "resume" => RecapExecutionCommands.ResumeAsync(
                options,
                completionClientFactory
            ),
            "restore" => RecapExecutionCommands.RestoreAsync(
                options,
                completionClientFactory
            ),
            "abandon-building" => AbandonBuildingAsync(options),
            "reset" => ResetAsync(options),
            _ => throw new ArgumentException(
                $"Unknown recap subcommand '{subcommand}'."
            )
        };
    }

    private static async Task<int> CreateAsync(CliOptions options) {
        RecapStoreCommandContext context = OpenContext(
            options,
            "input",
            "branch",
            "report-json"
        );
        using (context.Engine) {
            DerivedRecapStore store = DerivedRecapStore.Open(
                context.InputPath,
                context.Engine.BranchRefId
            );
            await store.CreateAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }

        var report = new RecapStoreOperationReport(
            OperationSchema,
            "create",
            context.BranchName,
            context.BranchRefId,
            Anchor: null,
            ResultType: "Created",
            QuarantineId: null,
            Reason: null
        );
        WriteReport(context.ReportPath, report);
        PrintOperation(report);
        return 0;
    }

    private static async Task<int> ResetAsync(CliOptions options) {
        RecapStoreCommandContext context = OpenContext(
            options,
            "input",
            "branch",
            "report-json",
            "confirm-ref"
        );
        using (context.Engine) {
            DerivedRecapStore store = DerivedRecapStore.Open(
                context.InputPath,
                context.Engine.BranchRefId
            );
            string confirmation =
                options.RequireSingle("confirm-ref");
            if (!string.Equals(
                    confirmation,
                    context.BranchRefId,
                    StringComparison.Ordinal
                )) {
                throw new ArgumentException(
                    "--confirm-ref must exactly match the selected "
                    + $"branch RefId '{context.BranchRefId}'."
                );
            }
            await store.ResetAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }

        var report = new RecapStoreOperationReport(
            OperationSchema,
            "reset",
            context.BranchName,
            context.BranchRefId,
            Anchor: null,
            ResultType: "Reset",
            QuarantineId: null,
            Reason: null
        );
        WriteReport(context.ReportPath, report);
        PrintOperation(report);
        return 0;
    }

    private static async Task<int> AbandonBuildingAsync(
        CliOptions options
    ) {
        RecapStoreCommandContext context = OpenContext(
            options,
            "input",
            "branch",
            "anchor",
            "report-json"
        );
        EventAddress anchor;
        QuarantineBuildingResult result;
        using (context.Engine) {
            DerivedRecapStore store = DerivedRecapStore.Open(
                context.InputPath,
                context.Engine.BranchRefId
            );
            anchor = ParseAnchor(options);
            result = await store.QuarantineBuildingAsync(
                    anchor,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }

        RecapStoreOperationReport report = result switch {
            QuarantineBuildingResult.Quarantined quarantined =>
                Operation(
                    context,
                    anchor,
                    nameof(QuarantineBuildingResult.Quarantined),
                    quarantined.QuarantineId
                ),
            QuarantineBuildingResult.AlreadyAbsent =>
                Operation(
                    context,
                    anchor,
                    nameof(QuarantineBuildingResult.AlreadyAbsent)
                ),
            QuarantineBuildingResult.PublishedConflict =>
                Operation(
                    context,
                    anchor,
                    nameof(QuarantineBuildingResult.PublishedConflict)
                ),
            QuarantineBuildingResult.Unavailable unavailable =>
                Operation(
                    context,
                    anchor,
                    nameof(QuarantineBuildingResult.Unavailable),
                    reason: unavailable.Reason
                ),
            _ => throw new InvalidDataException(
                $"Unknown Building quarantine result "
                + $"'{result.GetType().Name}'."
            )
        };
        WriteReport(context.ReportPath, report);
        PrintOperation(report);
        return result is QuarantineBuildingResult.PublishedConflict
            or QuarantineBuildingResult.Unavailable
            ? 2
            : 0;
    }

    private static async Task<int> InspectAsync(CliOptions options) {
        RecapStoreCommandContext context = OpenContext(
            options,
            "input",
            "branch",
            "anchor",
            "report-json"
        );
        EventAddress anchor;
        ExactBuildingInspectionReport building;
        ExactPublishedInspectionReport published;
        using (context.Engine) {
            DerivedRecapStore store = DerivedRecapStore.Open(
                context.InputPath,
                context.Engine.BranchRefId
            );
            DerivedRecapLineageView lineage =
                DerivedRecapLineageView.Capture(
                    store,
                    context.Engine.ReadView
                );
            anchor = ParseAnchor(options);
            PublishedMembershipInspectionResult membership =
                await store.InspectPublishedMembershipAsync(
                        anchor,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            building = membership
                is PublishedMembershipInspectionResult.StoreUnavailable
                    storeUnavailable
                ? new ExactBuildingInspectionReport(
                    "StoreUnavailable",
                    [],
                    [
                        new RecapCliDefectReport(
                            "StoreUnavailable",
                            storeUnavailable.Reason
                        )
                    ]
                )
                : await InspectBuildingAsync(
                    store,
                    anchor
                ).ConfigureAwait(false);
            published = await InspectPublishedAsync(
                    lineage,
                    anchor,
                    membership
                )
                .ConfigureAwait(false);
        }

        var report = new RecapStoreInspectionReport(
            InspectionSchema,
            context.BranchName,
            context.BranchRefId,
            SJ.EventAddressTextCodec.Format(anchor),
            building,
            published
        );
        WriteReport(context.ReportPath, report);
        Console.WriteLine($"anchor: {report.Anchor}");
        Console.WriteLine($"building: {report.Building.State}");
        Console.WriteLine(
            $"publishedMembership: {report.Published.Membership.State}"
        );
        Console.WriteLine(
            "publishedRestoreEligibility: "
            + report.Published.RestoreEligibility.State
        );
        return 0;
    }

    private static async ValueTask<ExactBuildingInspectionReport>
        InspectBuildingAsync(
        DerivedRecapStore store,
        EventAddress anchor
    ) {
        BuildingPlanReadResult planRead =
            await store.ReadBuildingPlanAsync(
                anchor,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        if (planRead is BuildingPlanReadResult.Missing) {
            return new ExactBuildingInspectionReport(
                "Missing",
                [],
                []
            );
        }
        if (planRead is BuildingPlanReadResult.Invalid planInvalid) {
            return new ExactBuildingInspectionReport(
                "Invalid",
                [],
                MapDefects(planInvalid.Defects)
            );
        }
        BuildingPlanSnapshot planSnapshot =
            ((BuildingPlanReadResult.Available)planRead).Snapshot;
        BuildingReadResult read = await store.ReadBuildingAsync(
                planSnapshot.Handle,
                CancellationToken.None
            )
            .ConfigureAwait(false);
        switch (read) {
            case BuildingReadResult.Missing:
                return new ExactBuildingInspectionReport(
                    "Missing",
                    [],
                    []
                );
            case BuildingReadResult.Invalid invalid:
                return new ExactBuildingInspectionReport(
                    "Invalid",
                    [],
                    MapDefects(invalid.Defects)
                );
            case BuildingReadResult.Available available:
                var blocks = new List<RecapBlockInspectionReport>(
                    available.Snapshot.Manifest.Blocks.Count
                );
                foreach (RecapBlockPlan plan
                         in available.Snapshot.Manifest.Blocks) {
                    BuildingBlockInspection inspection =
                        await store.InspectBuildingBlockAsync(
                                planSnapshot.Handle,
                                plan.RecapBlockId,
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                    blocks.Add(MapBuildingBlock(inspection));
                }
                return new ExactBuildingInspectionReport(
                    "Available",
                    blocks.AsReadOnly(),
                    []
                );
            default:
                throw new InvalidDataException(
                    $"Unknown Building read result "
                    + $"'{read.GetType().Name}'."
                );
        }
    }

    private static async ValueTask<ExactPublishedInspectionReport>
        InspectPublishedAsync(
        DerivedRecapLineageView lineage,
        EventAddress anchor,
        PublishedMembershipInspectionResult membership
    ) {
        var membershipReport = membership switch {
            PublishedMembershipInspectionResult.Present =>
                new ExactPublishedMembershipReport("Present", []),
            PublishedMembershipInspectionResult.Absent =>
                new ExactPublishedMembershipReport("Absent", []),
            PublishedMembershipInspectionResult.Invalid invalid =>
                new ExactPublishedMembershipReport(
                    "Invalid",
                    MapDefects(invalid.Defects)
                ),
            PublishedMembershipInspectionResult.StoreUnavailable
                    unavailable =>
                new ExactPublishedMembershipReport(
                    "StoreUnavailable",
                    [
                        new RecapCliDefectReport(
                            "StoreUnavailable",
                            unavailable.Reason
                        )
                    ]
                ),
            _ => throw new InvalidDataException(
                $"Unknown Published membership inspection "
                + $"'{membership.GetType().Name}'."
            )
        };
        if (membership
            is PublishedMembershipInspectionResult.Absent
                or PublishedMembershipInspectionResult.StoreUnavailable) {
            return new ExactPublishedInspectionReport(
                membershipReport,
                new PublishedRestoreEligibilityReport(
                    "NotApplicable",
                    AuthorityType: null,
                    Blocks: [],
                    Defects: []
                )
            );
        }

        PublishedRestoreInspectionResult result =
            await lineage.InspectPublishedForOfflineDiagnosticsAsync(
                    anchor,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        switch (result) {
            case PublishedRestoreInspectionResult.Unavailable unavailable:
                return new ExactPublishedInspectionReport(
                    membershipReport,
                    new PublishedRestoreEligibilityReport(
                        "Unavailable",
                        AuthorityType: null,
                        Blocks: [],
                        MapDefects(unavailable.Defects)
                    )
                );
            case PublishedRestoreInspectionResult.Available available:
                PublishedRestoreInspection inspection =
                    available.Inspection;
                RecapBlockInspectionReport[] blocks = [
                    .. inspection.FrozenPlan.Blocks.Select(plan =>
                        MapPublishedBlock(
                            plan,
                            inspection.Blocks[plan.RecapBlockId]
                        )
                    )
                ];
                return new ExactPublishedInspectionReport(
                    membershipReport,
                    new PublishedRestoreEligibilityReport(
                        "Available",
                        inspection.Handle.AuthorityKind.ToString(),
                        Array.AsReadOnly(blocks),
                        []
                    )
                );
            default:
                throw new InvalidDataException(
                    $"Unknown Published inspection result "
                    + $"'{result.GetType().Name}'."
                );
        }
    }

    private static RecapBlockInspectionReport MapBuildingBlock(
        BuildingBlockInspection inspection
    ) => new(
        inspection.Plan.RecapBlockId.Value,
        Mode(inspection.Plan),
        FinalState(inspection.Final),
        CheckpointState(inspection.Checkpoint),
        Capability: null,
        MapDefects(
            FinalDefects(inspection.Final)
                .Concat(CheckpointDefects(inspection.Checkpoint))
        )
    );

    private static RecapBlockInspectionReport MapPublishedBlock(
        RecapBlockPlan plan,
        PublishedBlockRestoreInspection inspection
    ) => new(
        plan.RecapBlockId.Value,
        Mode(plan),
        FinalState(inspection.Final),
        CheckpointState(inspection.Checkpoint),
        inspection.Capability.GetType().Name,
        MapDefects(
            FinalDefects(inspection.Final)
                .Concat(CheckpointDefects(inspection.Checkpoint))
                .Concat(CapabilityDefects(inspection.Capability))
        )
    );

    private static string Mode(RecapBlockPlan inspect) =>
        inspect switch {
            MaintainRecapBlockPlan => "Maintain",
            InheritRecapBlockPlan => "Inherit",
            _ => inspect.GetType().Name
        };

    private static string FinalState(FinalRecapBlockHealth inspect) =>
        inspect switch {
            FinalRecapBlockHealth.Missing => "Missing",
            FinalRecapBlockHealth.Healthy => "Healthy",
            FinalRecapBlockHealth.Damaged => "Damaged",
            FinalRecapBlockHealth.Unavailable => "Unavailable",
            _ => inspect.GetType().Name
        };

    private static string CheckpointState(
        RollingRecapCheckpointHealth inspect
    ) => inspect switch {
        RollingRecapCheckpointHealth.Missing => "Missing",
        RollingRecapCheckpointHealth.Healthy => "Healthy",
        RollingRecapCheckpointHealth.Unusable => "Unusable",
        RollingRecapCheckpointHealth.Unavailable => "Unavailable",
        _ => inspect.GetType().Name
    };

    private static IEnumerable<RecapStructuralDefect> FinalDefects(
        FinalRecapBlockHealth health
    ) => health switch {
        FinalRecapBlockHealth.Damaged damaged => damaged.Defects,
        FinalRecapBlockHealth.Unavailable unavailable =>
            unavailable.Defects,
        _ => []
    };

    private static IEnumerable<RecapStructuralDefect>
        CheckpointDefects(
        RollingRecapCheckpointHealth health
    ) => health switch {
        RollingRecapCheckpointHealth.Unusable unusable =>
            unusable.Defects,
        RollingRecapCheckpointHealth.Unavailable unavailable =>
            unavailable.Defects,
        _ => []
    };

    private static IEnumerable<RecapStructuralDefect>
        CapabilityDefects(
        PublishedBlockRestoreCapability capability
    ) => capability switch {
        PublishedBlockRestoreCapability.Unavailable unavailable =>
            unavailable.Defects,
        _ => []
    };

    private static IReadOnlyList<RecapCliDefectReport> MapDefects(
        IEnumerable<RecapStructuralDefect> defects
    ) => Array.AsReadOnly([
        .. defects
            .Select(static defect =>
                new RecapCliDefectReport(
                    defect.Code,
                    defect.Detail
                )
            )
            .Distinct()
    ]);

    private static RecapStoreOperationReport Operation(
        RecapStoreCommandContext context,
        EventAddress anchor,
        string resultType,
        string? quarantineId = null,
        string? reason = null
    ) => new(
        OperationSchema,
        "abandon-building",
        context.BranchName,
        context.BranchRefId,
        SJ.EventAddressTextCodec.Format(anchor),
        resultType,
        quarantineId,
        reason
    );

    private static EventAddress ParseAnchor(CliOptions options) {
        string value = options.RequireSingle("anchor");
        if (!SJ.EventAddressTextCodec.TryParse(
                value,
                out EventAddress anchor
            )) {
            throw new ArgumentException(
                $"--anchor is not a canonical EventAddress: '{value}'."
            );
        }
        return anchor;
    }

    private static RecapStoreCommandContext OpenContext(
        CliOptions options,
        params string[] allowed
    ) {
        options.EnsureOnly(allowed);
        string inputPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        string? reportPath =
            options.GetOptionalSingle("report-json");
        CliIo.EnsurePathChainHasNoReparsePoint(
            inputPath,
            "--input"
        );
        if (reportPath is not null) {
            CliIo.ValidateFileOutputPath(
                inputPath,
                reportPath,
                "--report-json"
            );
        }

        SJ.SessionJournalEngine engine =
            SJ.SessionJournalEngine.OpenReadOnly(
                inputPath,
                branchName
            );
        return new RecapStoreCommandContext(
            inputPath,
            engine.BranchName,
            engine.BranchRefId.ToHexString(),
            reportPath,
            engine
        );
    }

    private static void WriteReport<T>(string? path, T report) {
        if (path is not null) {
            CliIo.WriteJsonAtomically(path, report);
        }
    }

    private static void PrintOperation(
        RecapStoreOperationReport report
    ) {
        Console.WriteLine($"operation: {report.Operation}");
        Console.WriteLine($"branchRefId: {report.BranchRefId}");
        if (report.Anchor is not null) {
            Console.WriteLine($"anchor: {report.Anchor}");
        }
        Console.WriteLine($"result: {report.ResultType}");
        if (report.QuarantineId is not null) {
            Console.WriteLine(
                $"quarantineId: {report.QuarantineId}"
            );
        }
        if (report.Reason is not null) {
            Console.WriteLine($"reason: {report.Reason}");
        }
    }

    private sealed record RecapStoreCommandContext(
        string InputPath,
        string BranchName,
        string BranchRefId,
        string? ReportPath,
        SJ.SessionJournalEngine Engine
    );
}

internal sealed record RecapStoreOperationReport(
    string Schema,
    string Operation,
    string BranchName,
    string BranchRefId,
    string? Anchor,
    string ResultType,
    string? QuarantineId,
    string? Reason
);

internal sealed record RecapStoreInspectionReport(
    string Schema,
    string BranchName,
    string BranchRefId,
    string Anchor,
    ExactBuildingInspectionReport Building,
    ExactPublishedInspectionReport Published
);

internal sealed record ExactBuildingInspectionReport(
    string State,
    IReadOnlyList<RecapBlockInspectionReport> Blocks,
    IReadOnlyList<RecapCliDefectReport> Defects
);

internal sealed record ExactPublishedInspectionReport(
    ExactPublishedMembershipReport Membership,
    PublishedRestoreEligibilityReport RestoreEligibility
);

internal sealed record ExactPublishedMembershipReport(
    string State,
    IReadOnlyList<RecapCliDefectReport> Defects
);

internal sealed record PublishedRestoreEligibilityReport(
    string State,
    string? AuthorityType,
    IReadOnlyList<RecapBlockInspectionReport> Blocks,
    IReadOnlyList<RecapCliDefectReport> Defects
);

internal sealed record RecapBlockInspectionReport(
    string RecapBlockId,
    string Mode,
    string FinalState,
    string CheckpointState,
    string? Capability,
    IReadOnlyList<RecapCliDefectReport> Defects
);

internal sealed record RecapCliDefectReport(
    string Code,
    string Detail
);
