using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class RecapExecutionCommands {
    private const string ReportSchema =
        "atelia.session-journal.derived-recap-execution.v8";
    private const string DefaultCallLogDirectory =
        "gitignore/session-journal/recap-maintainer-calls";

    internal static Task<int> RunAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) => ExecuteAsync(
        options,
        completionClientFactory,
        explicitRebuild: false
    );

    internal static Task<int> RebuildAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) => ExecuteAsync(
        options,
        completionClientFactory,
        explicitRebuild: true
    );

    private static async Task<int> ExecuteAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory,
        bool explicitRebuild
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        options.EnsureOnly(explicitRebuild
            ? [
                "input", "branch", "connections", "connection",
                "call-log-dir", "report-json", "campaign",
                "reset", "confirm-ref"
            ]
            : [
                "input", "branch", "connections", "connection",
                "call-log-dir", "report-json"
            ]);

        string inputPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        string connectionsPath = options.RequireSingle("connections");
        string? requestedConnection =
            options.GetOptionalSingle("connection");
        string callLogDirectory =
            options.GetOptionalSingle("call-log-dir")
            ?? DefaultCallLogDirectory;
        string? reportPath = options.GetOptionalSingle("report-json");
        CliIo.EnsurePathChainHasNoReparsePoint(inputPath, "--input");
        CliIo.EnsurePathChainHasNoReparsePoint(
            connectionsPath,
            "--connections"
        );
        if (reportPath is not null) {
            CliIo.ValidateFileOutputPath(
                inputPath,
                reportPath,
                "--report-json"
            );
        }

        using SJ.SessionJournalEngine engine =
            SJ.SessionJournalEngine.OpenReadOnly(inputPath, branchName);
        ResolvedRecapPlannerComposition planning =
            BuiltInRecapPlannerConfig.Composition;
        DerivedRecapEpochStore store = DerivedRecapEpochStore.Open(
            inputPath,
            engine.BranchRefId,
            planning.StoreLimits
        );
        await store.EnsureCreatedAsync().ConfigureAwait(false);

        CompletionConnectionsFileConfig connections =
            CompletionConnectionConfigLoader.LoadFile(connectionsPath);
        using var registry = new CompletionConnectionRegistry(
            connections,
            completionClientFactory
        );
        CompletionConnectionConfig connection =
            registry.Resolve(requestedConnection);
        RecapCliMaintainerComposition? runtimeComposition = null;
        var maintainers = new DeferredRecapBlockMaintainerRegistry(() => {
            runtimeComposition ??= RecapCliComposition.CreateMaintainers(
                planning.CapabilityCatalog,
                connection,
                registry.GetClient(connection.Id),
                callLogDirectory,
                explicitRebuild ? "recap rebuild" : "recap run"
            );
            return runtimeComposition.Registry;
        });
        var executor = new DerivedRecapEpochCampaignExecutor(
            engine.ReadView,
            store,
            () => {
                ResolvedRecapPlannerComposition active =
                    BuiltInRecapPlannerConfig.Load(inputPath);
                return new RecapEpochActiveConfiguration(
                    active.Configuration,
                    active.OperationLimits,
                    active.StoreLimits
                );
            },
            planning.OperationLimits,
            maintainers
        );

        DerivedRecapEpochOperationResult result;
        string? campaign = null;
        if (explicitRebuild) {
            campaign = options.RequireSingle("campaign");
            bool reset = options.HasFlag("reset");
            if (reset) {
                string confirmation = options.RequireSingle("confirm-ref");
                if (!string.Equals(
                        confirmation,
                        engine.BranchRefId.ToHexString(),
                        StringComparison.Ordinal
                    )) {
                    throw new ArgumentException(
                        "--confirm-ref must exactly match the selected RefId."
                    );
                }
            }
            var spool = DerivedRecapRebuildSpoolStore.Open(
                inputPath,
                engine.BranchRefId
            );
            _ = await DerivedRecapFullRebuildAuthorityPreparer.BeginAsync(
                    engine,
                    spool,
                    campaign,
                    DerivedRecapRebuildSpoolLimits.Default
                )
                .ConfigureAwait(false);
            _ = await DerivedRecapFullRebuildAuthorityPreparer.ResumeAsync(
                    engine,
                    spool,
                    campaign
                )
                .ConfigureAwait(false);
            result = reset
                ? await executor.ResetAndRunExplicitRebuildAsync(
                        engine,
                        spool,
                        campaign
                    )
                    .ConfigureAwait(false)
                : await executor.RunExplicitRebuildAsync(
                        engine,
                        spool,
                        campaign
                    )
                    .ConfigureAwait(false);
        }
        else {
            result = await executor.RunOnlineAsync().ConfigureAwait(false);
        }

        RecapExecutionReport report = Map(
            explicitRebuild ? "rebuild" : "run",
            engine,
            campaign,
            result
        );
        if (reportPath is not null) {
            CliIo.WriteJsonAtomically(reportPath, report);
        }
        Console.WriteLine($"operation: recap {report.Operation}");
        Console.WriteLine($"status: {report.ResultStatus}");
        Console.WriteLine($"code: {report.Code}");
        Console.WriteLine($"capturedHead: {report.CapturedHead}");
        Console.WriteLine($"epochsPublished: {report.EpochsPublished}");
        Console.WriteLine($"maintainerCalls: {report.MaintainerCalls}");
        return report.ResultStatus switch {
            "Fresh" => 0,
            "MoreWorkPending" => 3,
            "FullRebuildRequired" => 4,
            _ => 2
        };
    }

    private static RecapExecutionReport Map(
        string operation,
        SJ.SessionJournalEngine engine,
        string? campaign,
        DerivedRecapEpochOperationResult result
    ) {
        string captured = engine.ReadCurrentLineageHeaders()
            .CapturedHead.ToString();
        return result switch {
            DerivedRecapEpochOperationResult.Fresh fresh => new(
                ReportSchema, operation, engine.BranchName,
                engine.BranchRefId.ToHexString(), captured, campaign,
                "Fresh", fresh.Reason, fresh.Reason,
                fresh.EpochsPublished, fresh.MaintainerCalls,
                fresh.Latest?.AdmissionAnchor.ToString()
            ),
            DerivedRecapEpochOperationResult.MoreWorkPending pending => new(
                ReportSchema, operation, engine.BranchName,
                engine.BranchRefId.ToHexString(), captured, campaign,
                "MoreWorkPending", "MoreWorkPending",
                "A complete epoch remains after the operation budget.",
                pending.EpochsPublished, pending.MaintainerCalls,
                pending.Latest.AdmissionAnchor.ToString()
            ),
            DerivedRecapEpochOperationResult.FullRebuildRequired rebuild =>
                new(
                    ReportSchema, operation, engine.BranchName,
                    engine.BranchRefId.ToHexString(), captured, campaign,
                    "FullRebuildRequired", rebuild.Reason.ToString(),
                    rebuild.Detail, 0, 0, null
                ),
            DerivedRecapEpochOperationResult.ConfigurationLimit limit => new(
                ReportSchema, operation, engine.BranchName,
                engine.BranchRefId.ToHexString(), captured, campaign,
                "Unavailable", "ConfigurationLimit", limit.Detail,
                0, 0, null
            ),
            DerivedRecapEpochOperationResult.Unavailable unavailable => new(
                ReportSchema, operation, engine.BranchName,
                engine.BranchRefId.ToHexString(), captured, campaign,
                "Unavailable", unavailable.Code, unavailable.Detail,
                0, 0, null
            ),
            DerivedRecapEpochOperationResult.BlockFailed failed => new(
                ReportSchema, operation, engine.BranchName,
                engine.BranchRefId.ToHexString(), captured, campaign,
                "BlockFailed", failed.Code, failed.Detail,
                0, 0, failed.AdmissionBoundary.ToString()
            ),
            _ => throw new InvalidDataException(
                "Unknown shared-epoch operation result."
            )
        };
    }
}

internal sealed record RecapExecutionReport(
    string Schema,
    string Operation,
    string Branch,
    string RefId,
    string CapturedHead,
    string? CampaignId,
    string ResultStatus,
    string Code,
    string Detail,
    int EpochsPublished,
    int MaintainerCalls,
    string? LatestAdmissionAnchor
);
