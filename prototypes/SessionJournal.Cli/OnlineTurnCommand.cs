using System.Security.Cryptography;
using System.Text;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Planner;
using Atelia.SessionJournal.DerivedRecap.Store;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal static class OnlineTurnCommand {
    private const string DefaultCallLogDirectory =
        "gitignore/session-journal/online-turn-calls";

    internal static async Task<int> RunAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(completionClientFactory);
        options.EnsureOnly(
            "input",
            "branch",
            "connections",
            "connection",
            "call-log-dir",
            "output",
            "message",
            "maximum-canonical-request-bytes",
            "uncertain-recovery"
        );

        string inputPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        string connectionsPath =
            options.RequireSingle("connections");
        string outputPath = options.RequireSingle("output");
        string callLogDirectory =
            options.GetOptionalSingle("call-log-dir")
            ?? DefaultCallLogDirectory;
        string? requestedConnection =
            options.GetOptionalSingle("connection");
        string? message = options.GetOptionalSingle("message");
        long? maximumCanonicalRequestBytes = ParsePositiveLong(
            options.GetOptionalSingle(
                "maximum-canonical-request-bytes"
            ),
            "--maximum-canonical-request-bytes"
        );
        SJ.SessionUncertainCompletionRecoveryPolicy recoveryPolicy =
            ParseRecoveryPolicy(
                options.GetOptionalSingle("uncertain-recovery")
            );
        ValidatePaths(
            inputPath,
            connectionsPath,
            outputPath,
            callLogDirectory
        );

        using SJ.SessionJournalEngine engine =
            SJ.SessionJournalEngine.Open(inputPath, branchName);
        SJ.SessionExecutionBoundaryInspection initial =
            engine.InspectExecutionBoundary();
        OnlineExecutionMode mode = Classify(initial);
        ValidateMessage(mode, message);

        DerivedRecapOnlineLifecycleCoordinator? recap = null;
        DerivedRecapStore? store = null;
        if (mode is OnlineExecutionMode.SendNewTurn
            or OnlineExecutionMode.CompleteObservation) {
            store = DerivedRecapStore.Open(
                inputPath,
                engine.BranchRefId
            );
            await RequireStoreReadyAsync(engine, store)
                .ConfigureAwait(false);
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
            && !registry.TryGet(requestedConnection, out _)) {
            throw new ArgumentException(
                $"Unknown completion connection "
                + $"'{requestedConnection}'."
            );
        }
        CompletionConnectionConfig connection =
            registry.Resolve(requestedConnection);
        ICompletionClient inner = registry.GetClient(connection.Id);
        ICompletionClient agentClient =
            mode == OnlineExecutionMode.ResumeStarted
                && recoveryPolicy
                    == SJ.SessionUncertainCompletionRecoveryPolicy
                        .Refuse
                ? inner
                : new LoggingCompletionClient(
                    inner,
                    connection,
                    callLogDirectory,
                    new CompletionCallLogContext(
                        Command: "run-online-turn/agent"
                    )
                );

        if (store is not null) {
            ResolvedRecapPlannerComposition plannerComposition =
                RecapCliComposition.ProductionComposition;
            RecapCliMaintainerComposition maintainers =
                RecapCliComposition.CreateMaintainers(
                    plannerComposition.CapabilityCatalog,
                    connection,
                    inner,
                    callLogDirectory,
                    "run-online-turn/maintenance"
                );
            recap = new DerivedRecapOnlineLifecycleCoordinator(
                engine,
                store,
                plannerComposition.PlanningInputs,
                plannerComposition.PlanningLimits,
                maintainers.Registry
            );
        }

        engine.UseRuntime(new SJ.SessionRuntime(
            agentClient,
            CompletionTarget: CompletionTargetIdentityFactory.Create(
                connection,
                inner
            ),
            MaxTokens: connection.MaxTokens,
            UncertainCompletionRecoveryPolicy: recoveryPolicy,
            ContextCandidateSource: recap,
            MaximumCanonicalRequestBytes:
                maximumCanonicalRequestBytes,
            ContextLifecycle: recap
        ));

        (
            ActionMessage resultMessage,
            CompletionDescriptor invocation,
            IReadOnlyList<string>? errors
        ) = mode == OnlineExecutionMode.SendNewTurn
            ? FromTurn(await engine.SendAsync(
                    message!,
                    CancellationToken.None
                )
                .ConfigureAwait(false))
            : FromResume(
                await engine.ResumeAsync(CancellationToken.None)
                    .ConfigureAwait(false),
                initial.Phase
            );

        SJ.SessionExecutionBoundaryInspection final =
            engine.InspectExecutionBoundary();
        var report = new OnlineTurnRunRecord(
            "atelia.session-journal.online-turn-run.v3",
            engine.BranchName,
            engine.BranchRefId.ToHexString(),
            final.Head is { } head
                ? SJ.EventAddressTextCodec.Format(head)
                : null,
            final.Phase.ToString(),
            invocation.ProviderId,
            invocation.ApiSpecId,
            invocation.Model,
            Convert.ToHexStringLower(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        resultMessage.GetFlattenedText()
                    )
                )
            ),
            errors?.Count ?? 0
        );
        CliIo.WriteJsonAtomically(outputPath, report);
        Console.WriteLine($"head: {report.Head}");
        Console.WriteLine($"phase: {report.Phase}");
        Console.WriteLine($"output: {Path.GetFullPath(outputPath)}");
        return 0;
    }

    private static OnlineExecutionMode Classify(
        SJ.SessionExecutionBoundaryInspection boundary
    ) => boundary.Phase switch {
        SJ.SessionExecutionPhase.Idle
            or SJ.SessionExecutionPhase.TurnFailed =>
            OnlineExecutionMode.SendNewTurn,
        SJ.SessionExecutionPhase.AwaitingAgentAction
            when boundary.HeadKind
                == SJ.SessionEventKind.ObservationAccepted =>
            OnlineExecutionMode.CompleteObservation,
        SJ.SessionExecutionPhase.AwaitingAgentAction
            when boundary.HeadKind
                == SJ.SessionEventKind.ToolResultObserved =>
            throw Unsupported(
                "Tool-result continuation requires an exact tool runtime."
            ),
        SJ.SessionExecutionPhase.AwaitingCompletionDispatch =>
            OnlineExecutionMode.ResumePrepared,
        SJ.SessionExecutionPhase.AwaitingCompletion =>
            OnlineExecutionMode.ResumeStarted,
        SJ.SessionExecutionPhase.AwaitingToolExecution =>
            throw Unsupported(
                "AwaitingToolExecution requires an exact tool runtime."
            ),
        SJ.SessionExecutionPhase.Empty =>
            throw new InvalidOperationException(
                "run-online-turn requires an initialized SessionJournal."
            ),
        _ => throw new InvalidOperationException(
            $"run-online-turn does not support phase "
            + $"'{boundary.Phase}' at head kind "
            + $"'{boundary.HeadKind}'."
        )
    };

    private static void ValidateMessage(
        OnlineExecutionMode mode,
        string? message
    ) {
        if (mode == OnlineExecutionMode.SendNewTurn) {
            if (message is null) {
                throw new ArgumentException(
                    "--message is required for Idle or TurnFailed."
                );
            }
            return;
        }
        if (message is not null) {
            throw new ArgumentException(
                "--message must be absent when resuming an existing turn."
            );
        }
    }

    private static async ValueTask RequireStoreReadyAsync(
        SJ.SessionJournalEngine engine,
        DerivedRecapStore store
    ) {
        SJ.SessionCurrentLineageSnapshot lineage =
            engine.ReadCurrentLineageHeaders();
        DerivedRecapSelection selection =
            await store.SelectNthPreviousAsync(lineage, 0)
                .ConfigureAwait(false);
        if (selection is DerivedRecapSelection.StoreUnavailable
                unavailable) {
            throw new InvalidDataException(
                "DerivedRecap Store is unavailable: "
                + unavailable.Reason
            );
        }
    }

    private static (
        ActionMessage Message,
        CompletionDescriptor Invocation,
        IReadOnlyList<string>? Errors
    ) FromTurn(SJ.TurnResult result) => (
        result.Message,
        result.Invocation,
        result.Errors
    );

    private static (
        ActionMessage Message,
        CompletionDescriptor Invocation,
        IReadOnlyList<string>? Errors
    ) FromResume(
        SJ.ResumeOutcome result,
        SJ.SessionExecutionPhase initialPhase
    ) {
        if (!result.Advanced
            || result.Message is null
            || result.Invocation is null) {
            throw new InvalidOperationException(
                $"run-online-turn could not advance restart phase "
                + $"'{initialPhase}'."
            );
        }
        return (result.Message, result.Invocation, result.Errors);
    }

    private static void ValidatePaths(
        string inputPath,
        string connectionsPath,
        string outputPath,
        string callLogDirectory
    ) {
        CliIo.ValidateReadOnlyWritablePaths(
            [
                (inputPath, "--input"),
                (connectionsPath, "--connections")
            ],
            [
                (outputPath, "--output"),
                (callLogDirectory, "--call-log-dir")
            ]
        );
        CliIo.ValidateFileOutputPath(
            inputPath,
            outputPath,
            "--output"
        );
        CliIo.ValidateDirectoryOutputPath(
            inputPath,
            callLogDirectory,
            "--call-log-dir"
        );
        CliIo.EnsurePathsDoNotNest(
            outputPath,
            callLogDirectory,
            "--output and --call-log-dir must be disjoint paths."
        );
    }

    private static long? ParsePositiveLong(
        string? value,
        string option
    ) {
        if (value is null) {
            return null;
        }
        if (!long.TryParse(value, out long parsed)
            || parsed <= 0) {
            throw new ArgumentException(
                $"{option} must be a positive Int64."
            );
        }
        return parsed;
    }

    private static SJ.SessionUncertainCompletionRecoveryPolicy
        ParseRecoveryPolicy(string? value) {
        value ??= "refuse";
        return value switch {
            "refuse" =>
                SJ.SessionUncertainCompletionRecoveryPolicy.Refuse,
            "restart-new-attempt" =>
                SJ.SessionUncertainCompletionRecoveryPolicy
                    .RestartWithNewAttempt,
            _ => throw new ArgumentException(
                "--uncertain-recovery must be refuse or "
                + "restart-new-attempt."
            )
        };
    }

    private static NotSupportedException Unsupported(string detail)
        => new($"run-online-turn cannot safely resume this phase. {detail}");

    private enum OnlineExecutionMode {
        SendNewTurn,
        CompleteObservation,
        ResumePrepared,
        ResumeStarted,
    }
}

internal sealed record OnlineTurnRunRecord(
    string Schema,
    string BranchName,
    string BranchRefId,
    string? Head,
    string Phase,
    string ProviderId,
    string ApiSpecId,
    string Model,
    string ActionSha256,
    int ErrorCount
);
