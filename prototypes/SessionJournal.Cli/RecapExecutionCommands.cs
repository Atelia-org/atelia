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
        "atelia.session-journal.derived-recap-execution.v4";
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
        SJ.SessionCurrentLineageSnapshot? lineage =
            operation == "run"
                ? null
                : engine.ReadCurrentLineageHeaders();
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

        PreparedRecapOperationAuthority? runAuthority = null;
        RecapMaintainerProfileCatalog? preparedCapabilityCatalog = null;
        ResolvedRecapPlannerComposition? plannerComposition = null;
        RecapExecutionConfigReport? readinessConfigReport = null;
        IReadOnlyList<string> readinessDefects;
        if (operation == "run") {
            RecapOperationReadinessResult readiness =
                await RecapOperationReadiness.PrepareAsync(
                        engine,
                        store
                    )
                    .ConfigureAwait(false);
            if (readiness
                is RecapOperationReadinessResult.Ready ready) {
                runAuthority = ready.Authority;
                lineage = ready.Lineage;
                preparedCapabilityCatalog = ready.CapabilityCatalog;
                plannerComposition = ready.Composition;
                readinessDefects = [];
            }
            else {
                var blocked =
                    (RecapOperationReadinessResult.Blocked)readiness;
                readinessDefects = Array.AsReadOnly([
                    .. blocked.Defects.Select(
                        static defect => defect.Code
                    )
                ]);
                readinessConfigReport =
                    blocked.Composition is null
                        ? null
                        : CreateConfigReport(blocked.Composition);
            }
        }
        else {
            readinessDefects =
                await InspectReadinessAsync(
                        operation,
                        store,
                        lineage!,
                        anchor
                    )
                    .ConfigureAwait(false);
        }
        if (readinessDefects.Count != 0) {
            string? retryableCode = readinessDefects.FirstOrDefault(
                static code => code
                    is DerivedRecapExecutionDefectCodes.RawHeadChanged
                        or DerivedRecapExecutionDefectCodes.SourceChanged
            );
            bool retryable = retryableCode is not null;
            return Finish(
                Report(
                    operation,
                    engine,
                    lineage?.CapturedHead
                        ?? engine.ReadCurrentHead()
                        ?? throw new InvalidDataException(
                            "Raw SessionJournal has no current head."
                        ),
                    retryable ? "Retryable" : "Unavailable",
                    anchor,
                    blockId: null,
                    code: retryableCode,
                    defectCodes: retryable ? [] : readinessDefects,
                    readinessConfigReport,
                    planningDiagnostics: null,
                    callLogCount: 0,
                    callLogDirectory
                ),
                reportPath,
                exitCode: retryable ? 3 : 2
            );
        }
        if (operation == "restore"
            && expectedRawHead != lineage!.CapturedHead) {
            return Finish(
                Report(
                    operation,
                    engine,
                    lineage!.CapturedHead,
                    "Retryable",
                    anchor,
                    blockId: null,
                    code:
                        DerivedRecapRestoreDefectCodes.RawHeadChanged,
                    defectCodes: [],
                    configReport: null,
                    planningDiagnostics: null,
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
        RecapExecutionConfigReport? planningConfigReport =
            plannerComposition is null
                ? null
                : CreateConfigReport(plannerComposition);
        RecapMaintainerProfileCatalog capabilityCatalog =
            preparedCapabilityCatalog
                ?? plannerComposition?.CapabilityCatalog
                ?? RecapMaintainerProfileCatalog.BuiltIn;
        RecapCliMaintainerComposition? composition = null;
        var maintainers =
            new DeferredRecapBlockMaintainerRegistry(() => {
                composition ??=
                    RecapCliComposition.CreateMaintainers(
                        capabilityCatalog,
                        connection,
                        registry.GetClient(connection.Id),
                        callLogDirectory,
                        $"recap {operation}"
                    );
                return composition.Registry;
            });

        RecapExecutionReport report;
        int exitCode;
        if (operation == "restore") {
            var executor = new DerivedRecapRestoreExecutor(
                engine,
                store,
                maintainers
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
                lineage!.CapturedHead,
                result,
                anchor,
                composition?.LoggingClients ?? [],
                callLogDirectory
            );
        }
        else if (operation == "resume") {
            var executor = new DerivedRecapBuildingExecutor(
                engine,
                store,
                maintainers
            );
            DerivedRecapExecutionResult result =
                await executor.ResumeAsync(anchor!.Value)
                    .ConfigureAwait(false);
            (report, exitCode) = MapExecution(
                operation,
                engine,
                lineage!.CapturedHead,
                result,
                anchor,
                configReport: null,
                planningDiagnostics: null,
                composition?.LoggingClients ?? [],
                callLogDirectory
            );
        }
        else {
            DerivedRecapExecutionResult result;
            DerivedRecapPlanningDiagnostics? planningDiagnostics;
            if (runAuthority
                is PreparedRecapOperationAuthority
                    .FrozenBuilding frozen) {
                var executor = new DerivedRecapBuildingExecutor(
                    engine,
                    store,
                    maintainers
                );
                result = await executor.ResumeAsync(
                        frozen.Descriptor
                    )
                    .ConfigureAwait(false);
                planningDiagnostics = null;
            }
            else {
                var newPlanning =
                    (PreparedRecapOperationAuthority.NewPlanning)
                    runAuthority!;
                var executor = new DerivedRecapPlannerExecutor(
                    engine,
                    store,
                    newPlanning.Configuration.PlanningInputs,
                    newPlanning.Configuration.PlanningLimits,
                    maintainers
                );
                result = await executor.RunAsync(newPlanning.Baseline)
                    .ConfigureAwait(false);
                planningDiagnostics =
                    executor.LastPlanningDiagnostics;
            }
            (report, exitCode) = MapExecution(
                operation,
                engine,
                lineage!.CapturedHead,
                result,
                requestedAnchor: null,
                planningConfigReport,
                planningDiagnostics,
                composition?.LoggingClients ?? [],
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
        DerivedRecapPlanningDiagnostics? planningDiagnostics,
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
                    CreatePlanningReport(planningDiagnostics),
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
                    CreatePlanningReport(planningDiagnostics),
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
                    CreatePlanningReport(planningDiagnostics),
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
                    CreatePlanningReport(planningDiagnostics),
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
                    CreatePlanningReport(planningDiagnostics),
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
                    planningDiagnostics: null,
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
                    planningDiagnostics: null,
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
                    planningDiagnostics: null,
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
                    planningDiagnostics: null,
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
        RecapExecutionPlanningReport? planningDiagnostics,
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
        planningDiagnostics,
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
            composition.Snapshot.CanonicalPath,
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
                        profile.Capability.PromptFingerprint,
                        profile.Capability.CapabilityFingerprint
                    )
                )
            ],
            document.Cadence.HistoryUnitLoadEstimatorId,
            document.Cadence.MinimumRecentHistoryLoad,
            document.Cadence.RecapBuildIntervalHistoryLoad,
            document.Limits.MaxRawGrowthEventCount,
            document.Limits.MaxRouteEndpointsPerBlock,
            document.Limits.MaxMaintainerCallsPerBuild,
            document.Limits.MaxRawEventsPerStep,
            document.Limits.MaxRawEventsPerBuild
        );
    }

    private static RecapExecutionPlanningReport? CreatePlanningReport(
        DerivedRecapPlanningDiagnostics? diagnostics
    ) => diagnostics switch {
        DerivedRecapPlanningDiagnostics.RawSafetyRejected rejected =>
            new RecapExecutionPlanningReport(
                "RawSafetyRejected",
                HistoryUnitLoadEstimatorId: null,
                GrowthHistoryLoad: null,
                SelectedAbsorbedHistoryLoad: null,
                SelectedRecentHistoryLoad: null,
                GrowthHistoryUnitCount: null,
                rejected.RawGrowthEventCount
            ),
        DerivedRecapPlanningDiagnostics.ExactSchedule exact =>
            new RecapExecutionPlanningReport(
                "ExactSchedule",
                exact.Measurement.HistoryUnitLoadEstimatorId,
                exact.Measurement.GrowthHistoryLoad.Value,
                exact.Measurement
                    .SelectedAbsorbedHistoryLoad?.Value,
                exact.Measurement.SelectedRecentHistoryLoad?.Value,
                exact.Measurement.GrowthHistoryUnitCount,
                exact.Measurement.RawGrowthEventCount
            ),
        null => null,
        _ => throw new InvalidDataException(
            "Unknown recap planning diagnostics."
        )
    };

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
    RecapExecutionPlanningReport? Planning,
    int CallLogCount,
    string? CallLogDirectory
);

internal sealed record RecapExecutionConfigReport(
    string Schema,
    string? Path,
    string ConfigSha256,
    string PlanningPolicy,
    IReadOnlyList<RecapExecutionCatalogReport> Catalog,
    string HistoryUnitLoadEstimatorId,
    long MinimumRecentHistoryLoad,
    long RecapBuildIntervalHistoryLoad,
    int MaxRawGrowthEventCount,
    int MaxRouteEndpointsPerBlock,
    int MaxMaintainerCallsPerBuild,
    int MaxRawEventsPerStep,
    int MaxRawEventsPerBuild
);

internal sealed record RecapExecutionPlanningReport(
    string MeasurementKind,
    string? HistoryUnitLoadEstimatorId,
    long? GrowthHistoryLoad,
    long? SelectedAbsorbedHistoryLoad,
    long? SelectedRecentHistoryLoad,
    int? GrowthHistoryUnitCount,
    int? RawGrowthEventCount
);

internal sealed record RecapExecutionCatalogReport(
    string MaintainerProfile,
    string RecapBlockId,
    string TargetCarrier,
    string TargetBlockKey,
    string MaintainerId,
    int MaxContentUtf8Bytes,
    string PromptFingerprint,
    string CapabilityFingerprint
);
