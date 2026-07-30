using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapStoreCommands {
    private const string OperationSchema =
        "atelia.session-journal.derived-recap-store-operation.v1";
    private const string InspectionSchema =
        "atelia.session-journal.derived-recap-store-inspection.v1";

    internal static Task<int> RunAsync(string[] args) {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0
            || args[0] is "-h" or "--help"
            || args[0].StartsWith("--", StringComparison.Ordinal)) {
            throw new ArgumentException(
                "recap requires one subcommand: create, inspect, "
                + "abandon-building, or reset."
            );
        }

        string subcommand = args[0];
        CliOptions options = CliOptions.Parse(args.Skip(1).ToArray());
        return subcommand switch {
            "create" => CreateAsync(options),
            "inspect" => InspectAsync(options),
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
            anchor = ParseAnchor(options);
            building = await InspectBuildingAsync(
                    store,
                    anchor
                )
                .ConfigureAwait(false);
            published = await InspectPublishedAsync(
                    store,
                    anchor,
                    context.Engine.ReadCurrentLineageHeaders()
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
        Console.WriteLine($"published: {report.Published.State}");
        return 0;
    }

    private static async ValueTask<ExactBuildingInspectionReport>
        InspectBuildingAsync(
        DerivedRecapStore store,
        EventAddress anchor
    ) {
        BuildingReadResult read = await store.ReadBuildingAsync(
                anchor,
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
                                available.Snapshot.Descriptor,
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
        DerivedRecapStore store,
        EventAddress anchor,
        SJ.SessionCurrentLineageSnapshot lineage
    ) {
        PublishedRestoreInspectionResult result =
            await store.InspectPublishedForRestoreAsync(
                    anchor,
                    lineage,
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        switch (result) {
            case PublishedRestoreInspectionResult.Unavailable unavailable:
                string state = unavailable.Defects.Any(
                    static defect =>
                        defect.Code == "PublishedMembershipMissing"
                )
                    ? "Missing"
                    : "Unavailable";
                return new ExactPublishedInspectionReport(
                    state,
                    AuthorityType: null,
                    Blocks: [],
                    MapDefects(unavailable.Defects)
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
                    "Available",
                    inspection.Handle.AuthorityKind.ToString(),
                    Array.AsReadOnly(blocks),
                    []
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
            CliIo.EnsurePathChainHasNoReparsePoint(
                reportPath,
                "--report-json"
            );
            CliIo.EnsurePathIsOutsideRepository(
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
