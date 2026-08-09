using Atelia.Completion;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapStoreCommands {
    private const string OperationSchema =
        "atelia.session-journal.derived-recap-store-operation.v8";
    private const string InspectionSchema =
        "atelia.session-journal.derived-recap-store-inspection.v8";

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
                "recap requires one subcommand: history-load, create, "
                + "inspect, materialize-inspect, run, rebuild, or reset."
            );
        }
        string subcommand = args[0];
        if (subcommand == "planner-config") {
            return RecapPlannerConfigCommands.RunAsync(args[1..]);
        }
        if (subcommand == "history-load") {
            return RecapHistoryLoadCommands.RunAsync(args[1..]);
        }
        CliOptions options = CliOptions.Parse(args[1..]);
        return subcommand switch {
            "create" => CreateAsync(options),
            "inspect" => InspectAsync(options),
            "materialize-inspect" =>
                RecapMaterializationInspectionCommands.RunAsync(options),
            "run" => RecapExecutionCommands.RunAsync(
                options,
                completionClientFactory
            ),
            "rebuild" => RecapExecutionCommands.RebuildAsync(
                options,
                completionClientFactory
            ),
            "reset" => ResetAsync(options),
            _ => throw new ArgumentException(
                $"Unknown recap subcommand '{subcommand}'."
            )
        };
    }

    private static async Task<int> CreateAsync(CliOptions options) {
        CommandContext context = Open(options, requireConfirmation: false);
        using (context.Engine) {
            DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
                context.InputPath,
                context.Engine.BranchRefId,
                BuiltInRecapPlannerConfig.Composition.StoreLimits
            );
            await store.EnsureCreatedAsync().ConfigureAwait(false);
        }
        return Finish(
            new RecapStoreOperationReport(
                OperationSchema,
                "create",
                context.BranchName,
                context.RefId,
                "Created"
            ),
            context.ReportPath
        );
    }

    private static async Task<int> ResetAsync(CliOptions options) {
        CommandContext context = Open(options, requireConfirmation: true);
        using (context.Engine) {
            DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
                context.InputPath,
                context.Engine.BranchRefId,
                BuiltInRecapPlannerConfig.Composition.StoreLimits
            );
            await store.ResetAsync().ConfigureAwait(false);
        }
        return Finish(
            new RecapStoreOperationReport(
                OperationSchema,
                "reset",
                context.BranchName,
                context.RefId,
                "Reset"
            ),
            context.ReportPath
        );
    }

    private static async Task<int> InspectAsync(CliOptions options) {
        CommandContext context = Open(options, requireConfirmation: false);
        RecapStoreInspectionReport report;
        using (context.Engine) {
            try {
                DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
                    context.InputPath,
                    context.Engine.BranchRefId,
                    BuiltInRecapPlannerConfig.Composition.StoreLimits
                );
                RecapEpochBuildingSelectionResult building =
                    await store.SelectBuildingAsync().ConfigureAwait(false);
                SJ.SessionCurrentLineagePrefix prefix =
                    context.Engine.ReadView.ReadCurrentLineagePrefix(513);
                RecapEpochSelectionResult published =
                    await store.SelectLatestAsync([
                        .. prefix.HeadToOldest.Select(
                            static item => item.Address
                        )
                    ]).ConfigureAwait(false);
                report = new RecapStoreInspectionReport(
                    InspectionSchema,
                    context.BranchName,
                    context.RefId,
                    DescribeBuilding(building),
                    DescribePublished(published)
                );
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException
            ) {
                report = new RecapStoreInspectionReport(
                    InspectionSchema,
                    context.BranchName,
                    context.RefId,
                    new RecapStageInspection("Unavailable", null),
                    new RecapStageInspection(
                        "Unavailable",
                        exception.Message
                    )
                );
            }
        }
        if (context.ReportPath is not null) {
            CliIo.WriteJsonAtomically(context.ReportPath, report);
        }
        Console.WriteLine("operation: recap inspect");
        Console.WriteLine($"building: {report.Building.Status}");
        Console.WriteLine($"published: {report.Published.Status}");
        return report.Published.Status == "Unavailable" ? 2 : 0;
    }

    private static RecapStageInspection DescribeBuilding(
        RecapEpochBuildingSelectionResult result
    ) => result switch {
        RecapEpochBuildingSelectionResult.Selected selected => new(
            "Selected",
            selected.Snapshot.Descriptor.AdmissionAnchor.ToString()
        ),
        RecapEpochBuildingSelectionResult.Empty => new("Empty", null),
        RecapEpochBuildingSelectionResult.Invalid invalid => new(
            "Invalid",
            $"{invalid.AdmissionAnchor}: {invalid.Detail}"
        ),
        _ => throw new InvalidDataException("Unknown Building selection.")
    };

    private static RecapStageInspection DescribePublished(
        RecapEpochSelectionResult result
    ) => result switch {
        RecapEpochSelectionResult.Selected selected => new(
            "Selected",
            selected.Descriptor.AdmissionAnchor.ToString()
        ),
        RecapEpochSelectionResult.Empty => new("Empty", null),
        RecapEpochSelectionResult.Invalid invalid => new(
            "Invalid",
            $"{invalid.AdmissionAnchor}: {invalid.Detail}"
        ),
        _ => throw new InvalidDataException("Unknown Published selection.")
    };

    private static CommandContext Open(
        CliOptions options,
        bool requireConfirmation
    ) {
        options.EnsureOnly(requireConfirmation
            ? ["input", "branch", "report-json", "confirm-ref"]
            : ["input", "branch", "report-json"]);
        string input = options.RequireSingle("input");
        string branch = options.RequireSingle("branch");
        string? report = options.GetOptionalSingle("report-json");
        CliIo.EnsurePathChainHasNoReparsePoint(input, "--input");
        if (report is not null) {
            CliIo.ValidateFileOutputPath(input, report, "--report-json");
        }
        SJ.SessionJournalEngine engine =
            SJ.SessionJournalEngine.OpenReadOnly(input, branch);
        string refId = engine.BranchRefId.ToHexString();
        if (requireConfirmation
            && !string.Equals(
                options.RequireSingle("confirm-ref"),
                refId,
                StringComparison.Ordinal
            )) {
            engine.Dispose();
            throw new ArgumentException(
                $"--confirm-ref must exactly match selected RefId '{refId}'."
            );
        }
        return new CommandContext(input, branch, refId, report, engine);
    }

    private static int Finish(
        RecapStoreOperationReport report,
        string? reportPath
    ) {
        if (reportPath is not null) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }
        Console.WriteLine($"operation: recap {report.Operation}");
        Console.WriteLine($"result: {report.ResultType}");
        return 0;
    }

    private sealed record CommandContext(
        string InputPath,
        string BranchName,
        string RefId,
        string? ReportPath,
        SJ.SessionJournalEngine Engine
    );
}

internal sealed record RecapStoreOperationReport(
    string Schema,
    string Operation,
    string Branch,
    string RefId,
    string ResultType
);

internal sealed record RecapStageInspection(string Status, string? Detail);

internal sealed record RecapStoreInspectionReport(
    string Schema,
    string Branch,
    string RefId,
    RecapStageInspection Building,
    RecapStageInspection Published
);
