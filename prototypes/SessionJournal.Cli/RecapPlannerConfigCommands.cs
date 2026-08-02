using Atelia.SessionJournal.DerivedRecap.Planner;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapPlannerConfigCommands {
    private const string ReportSchema =
        "atelia.session-journal.recap-planner-config-operation.v2";

    internal static Task<int> RunAsync(string[] args) {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0
            || args[0] is "-h" or "--help"
            || args[0].StartsWith("--", StringComparison.Ordinal)) {
            throw new ArgumentException(
                "recap planner-config requires one subcommand: "
                + "init or inspect."
            );
        }

        string operation = args[0];
        CliOptions options =
            CliOptions.Parse(args.Skip(1).ToArray());
        return Task.FromResult(operation switch {
            "init" => Initialize(options),
            "inspect" => Inspect(options),
            _ => throw new ArgumentException(
                $"Unknown recap planner-config subcommand "
                + $"'{operation}'."
            )
        });
    }

    private static int Initialize(CliOptions options) {
        (string input, string? reportPath) = ReadOptions(options);
        ResolvedRecapPlannerComposition composition =
            RecapCliComposition.DefaultComposition;
        RecapPlannerConfigInitializeResult result =
            RecapPlannerConfigInitializer.Initialize(
                input,
                composition.Snapshot.Document
            );
        RecapPlannerConfigCommandReport report = result switch {
            RecapPlannerConfigInitializeResult.Initialized initialized =>
                InitializedReport(initialized, composition),
            RecapPlannerConfigInitializeResult.AlreadyExists existing =>
                FailureReport(
                    "init",
                    "AlreadyExists",
                    existing.Path,
                    []
                ),
            RecapPlannerConfigInitializeResult.Invalid invalid =>
                FailureReport(
                    "init",
                    "Invalid",
                    invalid.Path,
                    Map(invalid.Defects)
                ),
            RecapPlannerConfigInitializeResult.Unavailable unavailable =>
                FailureReport(
                    "init",
                    "Unavailable",
                    unavailable.Path,
                    [new("Unavailable", unavailable.Reason)]
                ),
            _ => throw new InvalidDataException(
                "Unknown planner config initialize result."
            )
        };
        int exitCode = result
            is RecapPlannerConfigInitializeResult.Initialized
            ? 0
            : 2;
        return Finish(report, reportPath, exitCode);
    }

    private static RecapPlannerConfigCommandReport InitializedReport(
        RecapPlannerConfigInitializeResult.Initialized initialized,
        ResolvedRecapPlannerComposition composition
    ) {
        if (!string.Equals(
                initialized.ConfigSha256,
                composition.Snapshot.ConfigSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Initialized planner config hash does not match the "
                + "resolved composition snapshot."
            );
        }
        return ResolvedReport(
            "init",
            "Initialized",
            initialized.Path,
            composition
        );
    }

    private static int Inspect(CliOptions options) {
        (string input, string? reportPath) = ReadOptions(options);
        RecapPlannerCompositionLoadResult load =
            RecapPlannerCompositionLoader.Load(input);
        RecapPlannerConfigCommandReport report;
        int exitCode;
        switch (load) {
            case RecapPlannerCompositionLoadResult.Resolved resolved:
                report = ResolvedReport(
                    "inspect",
                    "Resolved",
                    resolved.Composition.Snapshot.CanonicalPath!,
                    resolved.Composition
                );
                exitCode = 0;
                break;
            case RecapPlannerCompositionLoadResult.Missing missing:
                report = FailureReport(
                    "inspect",
                    "Missing",
                    missing.Path,
                    []
                );
                exitCode = 2;
                break;
            case RecapPlannerCompositionLoadResult.Invalid invalid:
                report = FailureReport(
                    "inspect",
                    "Invalid",
                    invalid.Path,
                    Map(invalid.Defects),
                    invalid.Snapshot
                );
                exitCode = 2;
                break;
            case RecapPlannerCompositionLoadResult.Unavailable unavailable:
                report = FailureReport(
                    "inspect",
                    "Unavailable",
                    unavailable.Path,
                    [new("Unavailable", unavailable.Reason)],
                    unavailable.Snapshot
                );
                exitCode = 2;
                break;
            default:
                throw new InvalidDataException(
                    "Unknown planner config load result."
                );
        }
        return Finish(report, reportPath, exitCode);
    }

    private static (string Input, string? ReportPath) ReadOptions(
        CliOptions options
    ) {
        options.EnsureOnly("input", "report-json");
        string input = options.RequireSingle("input");
        string? reportPath =
            options.GetOptionalSingle("report-json");
        if (reportPath is not null) {
            CliIo.ValidateFileOutputPath(
                input,
                reportPath,
                "--report-json"
            );
        }
        return (input, reportPath);
    }

    private static RecapPlannerConfigCommandReport ResolvedReport(
        string operation,
        string status,
        string path,
        ResolvedRecapPlannerComposition composition
    ) {
        RecapPlannerConfigDocument document =
            composition.Snapshot.Document;
        return new RecapPlannerConfigCommandReport(
            ReportSchema,
            operation,
            status,
            SafeFullPath(path),
            document.Schema,
            composition.Snapshot.ConfigSha256,
            document.PlanningPolicy,
            new RecapPlannerConfigCadenceReport(
                document.Cadence.HistoryUnitLoadEstimatorId,
                document.Cadence.MinimumRecentHistoryLoad,
                document.Cadence.RecapBuildIntervalHistoryLoad
            ),
            Array.AsReadOnly([
                .. composition.ActiveProfiles.Select(
                    static profile =>
                        new RecapPlannerConfigCatalogReport(
                            profile.ProfileName,
                            profile.CatalogEntry.RecapBlockId.Value,
                            SJ.ContextHeaderCarrierTokens
                                .ToStorageToken(
                                    profile.CatalogEntry
                                        .Target.Carrier
                                ),
                            profile.CatalogEntry.Target.BlockKey,
                            profile.CatalogEntry.MaintainerId,
                            profile.CatalogEntry
                                .MaxContentUtf8Bytes,
                            profile.Capability.PromptFingerprint,
                            profile.Capability.CapabilityFingerprint
                        )
                )
            ]),
            new RecapPlannerConfigLimitsReport(
                document.Limits.MaxRawGrowthEventCount,
                document.Limits.MaxRouteEndpointsPerBlock,
                document.Limits.MaxMaintainerCallsPerBuild,
                document.Limits.MaxRawEventsPerStep,
                document.Limits.MaxRawEventsPerBuild
            ),
            []
        );
    }

    private static RecapPlannerConfigCommandReport FailureReport(
        string operation,
        string status,
        string path,
        IReadOnlyList<RecapPlannerConfigCommandDefect> defects,
        RecapPlannerConfigSnapshot? snapshot = null
    ) {
        RecapPlannerConfigDocument? document =
            snapshot?.Document;
        return new RecapPlannerConfigCommandReport(
            ReportSchema,
            operation,
            status,
            SafeFullPath(path),
            document?.Schema,
            snapshot?.ConfigSha256,
            document?.PlanningPolicy,
            document is null
                ? null
                : new RecapPlannerConfigCadenceReport(
                    document.Cadence.HistoryUnitLoadEstimatorId,
                    document.Cadence.MinimumRecentHistoryLoad,
                    document.Cadence.RecapBuildIntervalHistoryLoad
                ),
            [],
            document is null
                ? null
                : new RecapPlannerConfigLimitsReport(
                    document.Limits.MaxRawGrowthEventCount,
                    document.Limits.MaxRouteEndpointsPerBlock,
                    document.Limits.MaxMaintainerCallsPerBuild,
                    document.Limits.MaxRawEventsPerStep,
                    document.Limits.MaxRawEventsPerBuild
                ),
            defects
        );
    }

    private static IReadOnlyList<RecapPlannerConfigCommandDefect> Map(
        IEnumerable<RecapPlannerConfigDefect> defects
    ) => Array.AsReadOnly([
        .. defects.Select(static defect =>
            new RecapPlannerConfigCommandDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]);

    private static IReadOnlyList<RecapPlannerConfigCommandDefect> Map(
        IEnumerable<RecapPlannerConfigResolveDefect> defects
    ) => Array.AsReadOnly([
        .. defects.Select(static defect =>
            new RecapPlannerConfigCommandDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]);

    private static IReadOnlyList<RecapPlannerConfigCommandDefect> Map(
        IEnumerable<RecapPlannerCompositionLoadDefect> defects
    ) => Array.AsReadOnly([
        .. defects.Select(static defect =>
            new RecapPlannerConfigCommandDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]);

    private static int Finish(
        RecapPlannerConfigCommandReport report,
        string? reportPath,
        int exitCode
    ) {
        if (reportPath is not null) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }
        Console.WriteLine($"operation: planner-config {report.Operation}");
        Console.WriteLine($"status: {report.Status}");
        Console.WriteLine($"path: {report.Path}");
        if (report.ConfigSha256 is not null) {
            Console.WriteLine(
                $"configSha256: {report.ConfigSha256}"
            );
        }
        foreach (RecapPlannerConfigCommandDefect defect
                 in report.Defects) {
            Console.WriteLine(
                $"defect: {defect.Code}: {defect.Detail}"
            );
        }
        if (reportPath is not null) {
            Console.WriteLine(
                $"report: {Path.GetFullPath(reportPath)}"
            );
        }
        return exitCode;
    }

    private static string SafeFullPath(string path) {
        try {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException
        ) {
            return path;
        }
    }
}

internal sealed record RecapPlannerConfigCommandReport(
    string Schema,
    string Operation,
    string Status,
    string Path,
    string? ConfigSchema,
    string? ConfigSha256,
    string? PlanningPolicy,
    RecapPlannerConfigCadenceReport? Cadence,
    IReadOnlyList<RecapPlannerConfigCatalogReport> Catalog,
    RecapPlannerConfigLimitsReport? Limits,
    IReadOnlyList<RecapPlannerConfigCommandDefect> Defects
);

internal sealed record RecapPlannerConfigCadenceReport(
    string HistoryUnitLoadEstimatorId,
    long MinimumRecentHistoryLoad,
    long RecapBuildIntervalHistoryLoad
);

internal sealed record RecapPlannerConfigCatalogReport(
    string MaintainerProfile,
    string RecapBlockId,
    string TargetCarrier,
    string TargetBlockKey,
    string MaintainerId,
    int MaxContentUtf8Bytes,
    string PromptFingerprint,
    string CapabilityFingerprint
);

internal sealed record RecapPlannerConfigLimitsReport(
    int MaxRawGrowthEventCount,
    int MaxRouteEndpointsPerBlock,
    int MaxMaintainerCallsPerBuild,
    int MaxRawEventsPerStep,
    int MaxRawEventsPerBuild
);

internal sealed record RecapPlannerConfigCommandDefect(
    string Code,
    string Detail
);
