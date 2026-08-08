using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Maintainers;
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
        SJ.SessionRuntimeRecoveryRequirements recoveryRequirement =
            engine.InspectRuntimeRecoveryRequirements();
        OnlineExecutionMode mode = Classify(recoveryRequirement);
        ValidateMessage(mode, message);
        if (mode == OnlineExecutionMode.ResumeStarted
            && recoveryPolicy
                == SJ.SessionUncertainCompletionRecoveryPolicy.Refuse) {
            throw new InvalidOperationException(
                "The current completion attempt may already have reached "
                + "the provider. Choose restart-new-attempt explicitly "
                + "to accept possible duplicate execution."
            );
        }
        EventAddress expectedOnlineHead =
            recoveryRequirement.CapturedHead
            ?? throw new InvalidDataException(
                "Supported online phase requires a captured raw head."
            );

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

        CompletionConnectionConfig connection;
        ICompletionClient? inner = null;
        CompletionDispatchIdentity? dispatchIdentity = null;
        if (recoveryRequirement
            is SJ.SessionRuntimeRecoveryRequirements
                .FrozenCompletionRequired frozen) {
            if (requestedConnection is not null
                && !string.Equals(
                    requestedConnection,
                    frozen.CompletionTarget.ConnectionId,
                    StringComparison.Ordinal
                )) {
                throw new InvalidOperationException(
                    "Prepared completion recovery is bound to durable "
                    + $"connection '{frozen.CompletionTarget.ConnectionId}', "
                    + $"not requested connection '{requestedConnection}'."
                );
            }
            dispatchIdentity = ToCompletionIdentity(frozen);
            CompletionDispatchBindingResult binding =
                registry.BindExact(dispatchIdentity);
            if (binding is CompletionDispatchBindingResult.Unavailable
                unavailable) {
                throw RecoveryBindingUnavailable(unavailable);
            }
            var bound = AssertBound(binding);
            connection = bound.Connection;
            inner = bound.Client;
        }
        else {
            connection = registry.Resolve(requestedConnection);
            if (mode == OnlineExecutionMode.SendNewTurn) {
                expectedOnlineHead = ReconcileSelectedConnection(
                    engine,
                    recoveryRequirement,
                    connection
                ).Head;
            }
            else if (mode == OnlineExecutionMode.CompleteObservation) {
                ValidateNewRequestConnectionMatchesGoverningSetup(
                    engine,
                    recoveryRequirement,
                    connection
                );
            }
        }

        DerivedRecapOnlineLifecycleCoordinator? recap = null;
        DerivedRecapStore? store = null;
        PreparedRecapOperationAuthority? recapAuthority = null;
        RecapMaintainerProfileCatalog? recapCapabilityCatalog = null;
        ResolvedRecapPlannerComposition? recapComposition = null;
        if (mode is OnlineExecutionMode.SendNewTurn
            or OnlineExecutionMode.CompleteObservation) {
            store = DerivedRecapStore.Open(
                inputPath,
                engine.BranchRefId
            );
            RecapOperationReadinessResult readiness =
                await RecapOperationReadiness.PrepareAsync(
                        engine,
                        store
                    )
                    .ConfigureAwait(false);
            if (readiness
                is RecapOperationReadinessResult.Ready ready) {
                if (ready.Lineage.CapturedHead != expectedOnlineHead) {
                    throw new InvalidOperationException(
                        "DerivedRecap preparation captured a different raw "
                        + "head than the online operation; retry phase "
                        + "inspection and composition."
                    );
                }
                recapAuthority = ready.Authority;
                recapCapabilityCatalog = ready.CapabilityCatalog;
                recapComposition = ready.Composition;
            }
            else if (readiness
                is RecapOperationReadinessResult.Blocked blocked) {
                throw ReadinessBlocked(blocked);
            }
            else {
                throw new InvalidDataException(
                    "Unknown DerivedRecap readiness result."
                );
            }
        }
        inner ??= registry.GetClient(connection.Id);
        dispatchIdentity ??=
            CompletionDispatchIdentityFactory.Create(connection, inner);
        ICompletionClient agentClient =
            new LoggingCompletionClient(
                inner,
                connection,
                callLogDirectory,
                new CompletionCallLogContext(
                    Command: "run-online-turn/agent"
                )
            );

        if (store is not null && recapAuthority is not null) {
            var maintainers =
                new DeferredRecapBlockMaintainerRegistry(
                    () =>
                        RecapCliComposition.CreateMaintainers(
                            recapCapabilityCatalog
                                ?? throw new InvalidDataException(
                                    "Prepared DerivedRecap capability "
                                    + "catalog is missing."
                                ),
                            connection,
                            inner,
                            callLogDirectory,
                            "run-online-turn/maintenance"
                        ).Registry
                );
            recap = DerivedRecapOnlineLifecycleCoordinator.Create(
                engine.ReadView,
                store,
                recapAuthority,
                maintainers
            );
        }

        engine.UseRuntime(new SJ.SessionRuntime(
            agentClient,
            CompletionTarget:
                CompletionTargetIdentityFactory.Create(dispatchIdentity),
            MaxTokens: connection.MaxTokens,
            UncertainCompletionRecoveryPolicy: recoveryPolicy,
            ContextCandidateSource: recap,
            MaximumCanonicalRequestBytes:
                maximumCanonicalRequestBytes,
            ContextLifecycle: recap
        ));

        (
            CompletionDescriptor invocation,
            IReadOnlyList<string>? errors
        ) = mode == OnlineExecutionMode.SendNewTurn
            ? FromTurn(await engine.SendAsync(
                    expectedOnlineHead,
                    message!,
                    CancellationToken.None
                )
                .ConfigureAwait(false))
            : FromResume(
                await engine.ResumeAsync(
                        expectedOnlineHead,
                        CancellationToken.None
                    )
                    .ConfigureAwait(false),
                recoveryRequirement.Phase
            );

        SJ.SessionExecutionBoundaryInspection final =
            engine.InspectExecutionBoundary();
        var report = new OnlineTurnRunRecord(
            "atelia.session-journal.online-turn-run.v6",
            engine.BranchName,
            engine.BranchRefId.ToHexString(),
            final.Head is { } head
                ? SJ.EventAddressTextCodec.Format(head)
                : null,
            final.Phase.ToString(),
            invocation.ProviderId,
            invocation.ApiSpecId,
            invocation.Model,
            errors?.Count ?? 0,
            recapComposition is null
                ? null
                : CreateConfigReport(recapComposition),
            CreatePlanningReport(
                recap?.LastPlanningDiagnostics
            )
        );
        CliIo.WriteJsonAtomically(outputPath, report);
        Console.WriteLine($"head: {report.Head}");
        Console.WriteLine($"phase: {report.Phase}");
        Console.WriteLine($"output: {Path.GetFullPath(outputPath)}");
        return 0;
    }

    private static OnlineExecutionMode Classify(
        SJ.SessionRuntimeRecoveryRequirements requirement
    ) => requirement switch {
        SJ.SessionRuntimeRecoveryRequirements.NoRuntimeRequired
            when requirement.Phase == SJ.SessionExecutionPhase.Idle =>
            OnlineExecutionMode.SendNewTurn,
        SJ.SessionRuntimeRecoveryRequirements
                .FailedTurnMustBeAbandoned failed =>
            throw new InvalidOperationException(
                "The failed turn at exact head "
                + $"'{failed.FailedHead}' must be abandoned before "
                + "starting a new online turn."
            ),
        SJ.SessionRuntimeRecoveryRequirements.NewRequestRequired
            when requirement.HeadKind
                == SJ.SessionEventKind.ObservationAccepted =>
            OnlineExecutionMode.CompleteObservation,
        SJ.SessionRuntimeRecoveryRequirements.NewRequestRequired
            when requirement.HeadKind
                == SJ.SessionEventKind.ToolResultObserved =>
            throw Unsupported(
                "Tool-result continuation requires an exact tool runtime."
            ),
        SJ.SessionRuntimeRecoveryRequirements.FrozenCompletionRequired {
            DispatchState: SJ.SessionDurableDispatchState.NotStarted
        } =>
            OnlineExecutionMode.ResumePrepared,
        SJ.SessionRuntimeRecoveryRequirements.FrozenCompletionRequired {
            DispatchState:
                SJ.SessionDurableDispatchState.StartedOutcomeUncertain
        } =>
            OnlineExecutionMode.ResumeStarted,
        SJ.SessionRuntimeRecoveryRequirements.ToolContinuationRequired =>
            throw Unsupported(
                "AwaitingToolExecution requires an exact tool runtime."
            ),
        SJ.SessionRuntimeRecoveryRequirements.NoRuntimeRequired
            when requirement.Phase == SJ.SessionExecutionPhase.Empty =>
            throw new InvalidOperationException(
                "run-online-turn requires an initialized SessionJournal."
            ),
        _ => throw new InvalidOperationException(
            $"run-online-turn does not support phase "
            + $"'{requirement.Phase}' at head kind "
            + $"'{requirement.HeadKind}'."
        )
    };

    private static CompletionDispatchIdentity ToCompletionIdentity(
        SJ.SessionRuntimeRecoveryRequirements
            .FrozenCompletionRequired frozen
    ) => new(
        frozen.CompletionTarget.ConnectionId,
        frozen.CompletionTarget.Kind,
        frozen.CompletionTarget.ConnectionFingerprint,
        frozen.ClientName,
        frozen.ApiSpecId,
        frozen.CompletionTarget.RequestAdapterFingerprint
    );

    private static CompletionDispatchBindingResult.Bound AssertBound(
        CompletionDispatchBindingResult result
    ) => result as CompletionDispatchBindingResult.Bound
        ?? throw new InvalidDataException(
            "Completion exact binding returned an unknown result shape."
        );

    private static InvalidOperationException RecoveryBindingUnavailable(
        CompletionDispatchBindingResult.Unavailable unavailable
    ) => new(
        "Prepared completion runtime is unavailable "
        + $"({unavailable.Reason}): {unavailable.Detail}"
    );

    private static SJ.SessionGoverningSetup ReconcileSelectedConnection(
        SJ.SessionJournalEngine engine,
        SJ.SessionRuntimeRecoveryRequirements requirement,
        CompletionConnectionConfig connection
    ) {
        EventAddress capturedHead = requirement.CapturedHead
            ?? throw new InvalidDataException(
                "Desired setup reconciliation requires a captured raw head."
            );
        SJ.SessionGoverningSetup current =
            engine.ResolveGoverningSetup(capturedHead);
        SJ.SessionDesiredSetupReconciliationResult result =
            engine.ReconcileDesiredSetup(
                capturedHead,
                new SJ.SessionDesiredSetup(
                    connection.ModelId,
                    connection.CompletionSurfaceId,
                    current.SystemPrompt
                )
            );
        return result switch {
            SJ.SessionDesiredSetupReconciliationResult.Ready ready =>
                ready.GoverningSetup,
            SJ.SessionDesiredSetupReconciliationResult.Unavailable
                unavailable => throw new InvalidOperationException(
                    "Desired SessionJournal setup is unavailable "
                    + $"({unavailable.Reason}) at phase "
                    + $"'{unavailable.Phase}'."
                ),
            SJ.SessionDesiredSetupReconciliationResult.Retryable retryable =>
                throw new InvalidOperationException(
                    "SessionJournal head changed during desired setup "
                    + $"reconciliation. Expected '{retryable.ExpectedHead}', "
                    + $"observed '{retryable.ObservedHead}'; retry the turn."
                ),
            _ => throw new InvalidDataException(
                "Unknown desired setup reconciliation result."
            )
        };
    }

    private static void ValidateNewRequestConnectionMatchesGoverningSetup(
        SJ.SessionJournalEngine engine,
        SJ.SessionRuntimeRecoveryRequirements requirement,
        CompletionConnectionConfig connection
    ) {
        EventAddress capturedHead = requirement.CapturedHead
            ?? throw new InvalidDataException(
                "New-request recovery requires a captured raw head."
            );
        SJ.SessionGoverningSetup governing =
            engine.ResolveGoverningSetup(capturedHead);
        if (!string.Equals(
                governing.RuntimeConfig.ModelId,
                connection.ModelId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                governing.RuntimeConfig.CompletionSurfaceId,
                connection.CompletionSurfaceId,
                StringComparison.Ordinal
            )) {
            throw new InvalidOperationException(
                "The selected completion connection model/surface does not "
                + "match the governing setup of the already accepted "
                + "Observation. Resume with a matching connection; setup "
                + "cannot be changed inside an active turn."
            );
        }
    }

    private static void ValidateMessage(
        OnlineExecutionMode mode,
        string? message
    ) {
        if (mode == OnlineExecutionMode.SendNewTurn) {
            if (message is null) {
                throw new ArgumentException(
                    "--message is required for Idle."
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

    private static InvalidDataException ReadinessBlocked(
        RecapOperationReadinessResult.Blocked blocked
    ) => new(
        "DerivedRecap readiness failed: "
        + string.Join(
            "; ",
            blocked.Defects.Select(static defect =>
                $"{defect.Code}: {defect.Detail}"
            )
        )
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
                        profile.Capability.FamilyFingerprint,
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

    private static (
        CompletionDescriptor Invocation,
        IReadOnlyList<string>? Errors
    ) FromTurn(SJ.TurnResult result) => (
        result.Invocation,
        result.Errors
    );

    private static (
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
        return (result.Invocation, result.Errors);
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
    int ErrorCount,
    RecapExecutionConfigReport? Config,
    RecapExecutionPlanningReport? Planning
);
