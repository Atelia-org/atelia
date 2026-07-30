using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapExecutionCommands {
    private const string ReportSchema =
        "atelia.session-journal.derived-recap-execution.v2";
    private const string DefaultCallLogDirectory =
        "gitignore/session-journal/recap-maintainer-calls";

    internal static Task<int> RunAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) => ExecuteAsync(
        "run",
        options,
        completionClientFactory
    );

    internal static Task<int> ResumeAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) => ExecuteAsync(
        "resume",
        options,
        completionClientFactory
    );

    internal static Task<int> RestoreAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) => ExecuteAsync(
        "restore",
        options,
        completionClientFactory
    );

    private static async Task<int> ExecuteAsync(
        string operation,
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        bool needsAnchor = operation is "resume" or "restore";
        options.EnsureOnly(
            needsAnchor
                ? operation == "restore"
                    ? [
                        "input",
                        "branch",
                        "connections",
                        "connection",
                        "call-log-dir",
                        "report-json",
                        "anchor",
                        "expected-raw-head"
                    ]
                    : [
                        "input",
                        "branch",
                        "connections",
                        "connection",
                        "call-log-dir",
                        "report-json",
                        "anchor"
                    ]
                : [
                    "input",
                    "branch",
                    "connections",
                    "connection",
                    "call-log-dir",
                    "report-json"
                ]
        );

        string inputPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        string connectionsPath =
            options.RequireSingle("connections");
        string? requestedConnection =
            options.GetOptionalSingle("connection");
        string callLogDirectory =
            options.GetOptionalSingle("call-log-dir")
            ?? DefaultCallLogDirectory;
        string? reportPath =
            options.GetOptionalSingle("report-json");
        ValidatePaths(
            inputPath,
            connectionsPath,
            callLogDirectory,
            reportPath
        );

        using SJ.SessionJournalEngine engine =
            SJ.SessionJournalEngine.OpenReadOnly(
                inputPath,
                branchName
            );
        DerivedRecapStore store = DerivedRecapStore.Open(
            inputPath,
            engine.BranchRefId
        );
        SJ.SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        EventAddress? anchor = needsAnchor
            ? ParseAddress(
                options.RequireSingle("anchor"),
                "--anchor"
            )
            : null;
        EventAddress? expectedRawHead = operation == "restore"
            ? ParseAddress(
                options.RequireSingle("expected-raw-head"),
                "--expected-raw-head"
            )
            : null;

        IReadOnlyList<string> readinessDefects =
            await InspectReadinessAsync(
                    operation,
                    store,
                    lineage,
                    anchor
                )
                .ConfigureAwait(false);
        if (readinessDefects.Count != 0) {
            return Finish(
                Report(
                    operation,
                    engine,
                    lineage.CapturedHead,
                    "Unavailable",
                    anchor,
                    blockId: null,
                    code: null,
                    readinessDefects,
                    configReport: null,
                    callLogCount: 0,
                    callLogDirectory
                ),
                reportPath,
                exitCode: 2
            );
        }
        if (operation == "restore"
            && expectedRawHead != lineage.CapturedHead) {
            return Finish(
                Report(
                    operation,
                    engine,
                    lineage.CapturedHead,
                    "Retryable",
                    anchor,
                    blockId: null,
                    code:
                        DerivedRecapRestoreDefectCodes.RawHeadChanged,
                    defectCodes: [],
                    configReport: null,
                    callLogCount: 0,
                    callLogDirectory: callLogDirectory
                ),
                reportPath,
                exitCode: 3
            );
        }

        CompletionConnectionsFileConfig connections =
            CompletionConnectionConfigLoader.LoadFile(
                connectionsPath
            );
        using var registry = new CompletionConnectionRegistry(
            connections,
            completionClientFactory
        );
        if (requestedConnection is not null
            && !registry.TryGet(
                requestedConnection,
                out _
            )) {
            throw new ArgumentException(
                $"Unknown completion connection "
                + $"'{requestedConnection}'."
            );
        }
        CompletionConnectionConfig connection =
            registry.Resolve(requestedConnection);
        ICompletionClient inner =
            registry.GetClient(connection.Id);
        ResolvedRecapPlannerComposition? plannerComposition =
            operation == "run"
                ? RecapCliComposition.ProductionComposition
                : null;
        RecapExecutionConfigReport? planningConfigReport =
            plannerComposition is null
                ? null
                : CreateConfigReport(plannerComposition);
        RecapMaintainerProfileCatalog capabilityCatalog =
            plannerComposition?.CapabilityCatalog
            ?? RecapMaintainerProfileCatalog.BuiltIn;
        RecapCliMaintainerComposition composition =
            RecapCliComposition.CreateMaintainers(
                capabilityCatalog,
                connection,
                inner,
                callLogDirectory,
                $"recap {operation}"
            );

        RecapExecutionReport report;
        int exitCode;
        if (operation == "restore") {
            var executor = new DerivedRecapRestoreExecutor(
                engine,
                store,
                composition.Registry
            );
            DerivedRecapRestoreResult result =
                await executor.RestoreAsync(
                        anchor!.Value,
                        expectedRawHead!.Value
                    )
                    .ConfigureAwait(false);
            (report, exitCode) = MapRestore(
                operation,
                engine,
                lineage.CapturedHead,
                result,
                anchor,
                composition.LoggingClients,
                callLogDirectory
            );
        }
        else if (operation == "resume") {
            var executor = new DerivedRecapBuildingExecutor(
                engine,
                store,
                composition.Registry
            );
            DerivedRecapExecutionResult result =
                await executor.ResumeAsync(anchor!.Value)
                    .ConfigureAwait(false);
            (report, exitCode) = MapExecution(
                operation,
                engine,
                lineage.CapturedHead,
                result,
                anchor,
                configReport: null,
                composition.LoggingClients,
                callLogDirectory
            );
        }
        else {
            var executor = new DerivedRecapPlannerExecutor(
                engine,
                store,
                plannerComposition!.PlanningInputs,
                plannerComposition.PlanningLimits,
                composition.Registry
            );
            DerivedRecapExecutionResult result =
                await executor.RunAsync().ConfigureAwait(false);
            (report, exitCode) = MapExecution(
                operation,
                engine,
                lineage.CapturedHead,
                result,
                requestedAnchor: null,
                planningConfigReport,
                composition.LoggingClients,
                callLogDirectory
            );
        }
        return Finish(report, reportPath, exitCode);
    }

    private static async ValueTask<IReadOnlyList<string>>
        InspectReadinessAsync(
        string operation,
        DerivedRecapStore store,
        SJ.SessionCurrentLineageSnapshot lineage,
        EventAddress? anchor
    ) {
        try {
            if (operation == "run") {
                DerivedRecapSelection selection =
                    await store.SelectNthPreviousAsync(lineage, 0)
                        .ConfigureAwait(false);
                return selection switch {
                    DerivedRecapSelection.StoreUnavailable =>
                        ["StoreUnavailable"],
                    DerivedRecapSelection
                            .ExactPublishedSetInvalid invalid =>
                        DefectCodes(invalid.Defects),
                    _ => []
                };
            }
            if (operation == "resume") {
                BuildingReadResult building =
                    await store.ReadBuildingAsync(anchor!.Value)
                        .ConfigureAwait(false);
                return building switch {
                    BuildingReadResult.Available => [],
                    BuildingReadResult.Invalid invalid =>
                        DefectCodes(invalid.Defects),
                    BuildingReadResult.Missing => ["BuildingMissing"],
                    _ => ["BuildingUnavailable"]
                };
            }

            PublishedRestoreInspectionResult published =
                await store.InspectPublishedForRestoreAsync(
                        anchor!.Value,
                        lineage
                    )
                    .ConfigureAwait(false);
            return published switch {
                PublishedRestoreInspectionResult.Available => [],
                PublishedRestoreInspectionResult
                        .Unavailable unavailable =>
                    DefectCodes(unavailable.Defects),
                _ => ["PublishedUnavailable"]
            };
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or NotSupportedException
                  or IOException
                  or UnauthorizedAccessException) {
            return ["StoreUnavailable"];
        }
    }

    private static (
        RecapExecutionReport Report,
        int ExitCode
    ) MapExecution(
        string operation,
        SJ.SessionJournalEngine engine,
        EventAddress rawHead,
        DerivedRecapExecutionResult result,
        EventAddress? requestedAnchor,
        RecapExecutionConfigReport? configReport,
        IReadOnlyList<LoggingCompletionClient> loggingClients,
        string callLogDirectory
    ) {
        int calls = loggingClients.Sum(
            static client => client.WrittenCallLogPaths.Count
        );
        return result switch {
            DerivedRecapExecutionResult.Published published => (
                Report(
                    operation,
                    engine,
                    rawHead,
                    "Published",
                    published.Descriptor.SetAdmissionAnchor,
                    null,
                    null,
                    [],
                    configReport,
                    calls,
                    callLogDirectory
                ),
                0
            ),
            DerivedRecapExecutionResult.NoBuild => (
                Report(
                    operation,
                    engine,
                    rawHead,
                    "NoBuild",
                    null,
                    null,
                    null,
                    [],
                    configReport,
                    calls,
                    callLogDirectory
                ),
                0
            ),
            DerivedRecapExecutionResult.Unavailable unavailable => (
                Report(
                    operation,
                    engine,
                    rawHead,
                    "Unavailable",
                    requestedAnchor,
                    null,
                    null,
                    unavailable.Defects.Select(
                        static defect => defect.Code
                    ),
                    configReport,
                    calls,
                    callLogDirectory
                ),
                2
            ),
            DerivedRecapExecutionResult.BlockFailed failed => (
                Report(
                    operation,
                    engine,
                    rawHead,
                    "BlockFailed",
                    failed.SetAdmissionAnchor,
                    failed.RecapBlockId.Value,
                    failed.Code,
                    [],
                    configReport,
                    calls,
                    callLogDirectory
                ),
                2
            ),
            DerivedRecapExecutionResult.Retryable retryable => (
                Report(
                    operation,
                    engine,
                    rawHead,
                    "Retryable",
                    requestedAnchor,
                    null,
                    retryable.Code,
                    [],
                    configReport,
                    calls,
                    callLogDirectory
                ),
                3
            ),
            _ => throw new InvalidDataException(
                $"Unknown Recap execution result "
                + $"'{result.GetType().Name}'."
            )
        };
    }

    private static (
        RecapExecutionReport Report,
        int ExitCode
    ) MapRestore(
        string operation,
        SJ.SessionJournalEngine engine,
        EventAddress rawHead,
        DerivedRecapRestoreResult result,
        EventAddress? requestedAnchor,
        IReadOnlyList<LoggingCompletionClient> loggingClients,
        string callLogDirectory
    ) {
        int calls = loggingClients.Sum(
            static client => client.WrittenCallLogPaths.Count
        );
        return result switch {
            DerivedRecapRestoreResult.Restored restored => (
                Report(
                    operation,
                    engine,
                    rawHead,
                    "Restored",
                    restored.Descriptor.SetAdmissionAnchor,
                    null,
                    null,
                    [],
                    configReport: null,
                    calls,
                    callLogDirectory
                ),
                0
            ),
            DerivedRecapRestoreResult.Unavailable unavailable => (
                Report(
                    operation,
                    engine,
                    rawHead,
                    "Unavailable",
                    requestedAnchor,
                    null,
                    null,
                    unavailable.Defects.Select(
                        static defect => defect.Code
                    ),
                    configReport: null,
                    calls,
                    callLogDirectory
                ),
                2
            ),
            DerivedRecapRestoreResult.BlockFailed failed => (
                Report(
                    operation,
                    engine,
                    rawHead,
                    "BlockFailed",
                    requestedAnchor,
                    failed.RecapBlockId.Value,
                    failed.Code,
                    [],
                    configReport: null,
                    calls,
                    callLogDirectory
                ),
                2
            ),
            DerivedRecapRestoreResult.Retryable retryable => (
                Report(
                    operation,
                    engine,
                    rawHead,
                    "Retryable",
                    requestedAnchor,
                    null,
                    retryable.Code,
                    [],
                    configReport: null,
                    calls,
                    callLogDirectory
                ),
                3
            ),
            _ => throw new InvalidDataException(
                $"Unknown Recap restore result "
                + $"'{result.GetType().Name}'."
            )
        };
    }

    private static RecapExecutionReport Report(
        string operation,
        SJ.SessionJournalEngine engine,
        EventAddress rawHead,
        string resultStatus,
        EventAddress? anchor,
        string? blockId,
        string? code,
        IEnumerable<string> defectCodes,
        RecapExecutionConfigReport? configReport,
        int callLogCount,
        string callLogDirectory
    ) => new(
        ReportSchema,
        operation,
        engine.BranchName,
        engine.BranchRefId.ToHexString(),
        SJ.EventAddressTextCodec.Format(rawHead),
        resultStatus,
        anchor is { } exact
            ? SJ.EventAddressTextCodec.Format(exact)
            : null,
        blockId,
        code,
        Array.AsReadOnly(
            defectCodes
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        ),
        configReport,
        callLogCount,
        Path.GetFullPath(callLogDirectory)
    );

    private static RecapExecutionConfigReport CreateConfigReport(
        ResolvedRecapPlannerComposition composition
    ) {
        RecapPlannerConfigDocument document =
            composition.Snapshot.Document;
        return new RecapExecutionConfigReport(
            document.Schema,
            composition.Snapshot.ConfigSha256,
            document.PlanningPolicy,
            [
                .. composition.ActiveProfiles.Select(
                    static profile => new RecapExecutionCatalogReport(
                        profile.ProfileName,
                        profile.CatalogEntry.RecapBlockId.Value,
                        SJ.ContextHeaderCarrierTokens.ToStorageToken(
                            profile.CatalogEntry.Target.Carrier
                        ),
                        profile.CatalogEntry.Target.BlockKey,
                        profile.CatalogEntry.MaintainerId,
                        profile.CatalogEntry.MaxContentUtf8Bytes,
                        profile.Capability.PromptFingerprint
                    )
                )
            ],
            document.Cadence.MinimumRecentHistoryUnitCount,
            document.Cadence.RecapBuildIntervalUnitCount,
            document.Limits.MaxRawGrowthEventCount,
            document.Limits.MaxRouteEndpointsPerBlock,
            document.Limits.MaxMaintainerCallsPerBuild,
            document.Limits.MaxRawEventsPerStep,
            document.Limits.MaxRawEventsPerBuild
        );
    }

    private static int Finish(
        RecapExecutionReport report,
        string? reportPath,
        int exitCode
    ) {
        if (reportPath is not null) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }
        Console.WriteLine($"operation: {report.Operation}");
        Console.WriteLine($"rawHead: {report.RawHead}");
        Console.WriteLine($"result: {report.ResultStatus}");
        if (report.Anchor is not null) {
            Console.WriteLine($"anchor: {report.Anchor}");
        }
        if (report.BlockId is not null) {
            Console.WriteLine($"blockId: {report.BlockId}");
        }
        if (report.Code is not null) {
            Console.WriteLine($"code: {report.Code}");
        }
        return exitCode;
    }

    private static EventAddress ParseAddress(
        string value,
        string option
    ) {
        if (!SJ.EventAddressTextCodec.TryParse(
                value,
                out EventAddress address
            )) {
            throw new ArgumentException(
                $"{option} is not a canonical EventAddress: '{value}'."
            );
        }
        return address;
    }

    private static IReadOnlyList<string> DefectCodes(
        IEnumerable<RecapStructuralDefect> defects
    ) => Array.AsReadOnly(
        defects
            .Select(static defect => defect.Code)
            .Distinct(StringComparer.Ordinal)
            .ToArray()
    );

    private static void ValidatePaths(
        string inputPath,
        string connectionsPath,
        string callLogDirectory,
        string? reportPath
    ) {
        CliIo.ValidateReadOnlyWritablePaths(
            [
                (inputPath, "--input"),
                (connectionsPath, "--connections")
            ],
            [
                (callLogDirectory, "--call-log-dir"),
                .. reportPath is null
                    ? []
                    : new[] {
                        (reportPath, "--report-json")
                    }
            ]
        );
        CliIo.ValidateDirectoryOutputPath(
            inputPath,
            callLogDirectory,
            "--call-log-dir"
        );
        if (reportPath is not null) {
            CliIo.ValidateFileOutputPath(
                inputPath,
                reportPath,
                "--report-json"
            );
            CliIo.EnsurePathsDoNotNest(
                callLogDirectory,
                reportPath,
                "--call-log-dir and --report-json must be disjoint."
            );
        }
    }
}

internal sealed record RecapExecutionReport(
    string Schema,
    string Operation,
    string BranchName,
    string BranchRefId,
    string RawHead,
    string ResultStatus,
    string? Anchor,
    string? BlockId,
    string? Code,
    IReadOnlyList<string> DefectCodes,
    RecapExecutionConfigReport? Config,
    int CallLogCount,
    string? CallLogDirectory
);

internal sealed record RecapExecutionConfigReport(
    string Schema,
    string ConfigSha256,
    string PlanningPolicy,
    IReadOnlyList<RecapExecutionCatalogReport> Catalog,
    int MinimumRecentHistoryUnitCount,
    int RecapBuildIntervalUnitCount,
    int MaxRawGrowthEventCount,
    int MaxRouteEndpointsPerBlock,
    int MaxMaintainerCallsPerBuild,
    int MaxRawEventsPerStep,
    int MaxRawEventsPerBuild
);

internal sealed record RecapExecutionCatalogReport(
    string MaintainerProfile,
    string RecapBlockId,
    string TargetCarrier,
    string TargetBlockKey,
    string MaintainerId,
    int MaxContentUtf8Bytes,
    string PromptFingerprint
);
