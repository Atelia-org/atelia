using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;
using Atelia.SessionJournal.RecapGrid.AgentControl;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCandidateCommands {
    private static async ValueTask<int> RunOnlineTurnAsync(
        CliOptions options,
        ICompletionClientFactory completionClientFactory
    ) {
        options.EnsureOnly(
            "input",
            "branch",
            "confirm-ref",
            "connections",
            "routes",
            "connection",
            "message",
            "maximum-canonical-request-bytes",
            "uncertain-recovery",
            "admission"
        );
        string repositoryPath = options.RequireSingle("input");
        string branchName = options.RequireSingle("branch");
        CliIo.EnsurePathChainHasNoReparsePoint(repositoryPath, "--input");
        using SessionJournalEngine engine = SessionJournalEngine.Open(
            repositoryPath,
            branchName
        );
        RequireConfirmedRef(options, engine.BranchRefId);

        SessionRuntimeRecoveryRequirements recovery =
            engine.InspectRuntimeRecoveryRequirements();
        CandidateOnlineMode mode = ClassifyOnlineMode(recovery);
        string? message = options.GetOptionalSingle("message");
        ValidateOnlineMessage(mode, message);
        SessionUncertainCompletionRecoveryPolicy recoveryPolicy =
            ParseOnlineRecoveryPolicy(
                options.GetOptionalSingle("uncertain-recovery")
            );

        // A Started/Refuse operation is intentionally decided before reading
        // connection or route manifests and before constructing any client.
        if (mode == CandidateOnlineMode.ResumeStarted
            && recoveryPolicy
                == SessionUncertainCompletionRecoveryPolicy.Refuse) {
            return Print(
                "run-online-turn",
                "started-outcome-uncertain",
                new {
                    nextAction = "retry-with-restart-new-attempt",
                    head = FormatAddress(recovery.CapturedHead)
                },
                exitCode: 2
            );
        }

        RecapGridAgentControlProfile? agentProfile = null;
        if (options.GetOptionalSingle("admission") is not null) {
            agentProfile = RecapGridAgentControlProfile.Create(
                "candidate-cli-v1",
                ReadAdmission(options)
            );
        }
        if (mode is not CandidateOnlineMode.ResumePrepared
            && agentProfile is null) {
            throw new ArgumentException(
                "--admission is required for an Agent Control candidate turn."
            );
        }
        RecapGridAgentControlProfileRegistry? agentProfiles =
            agentProfile is null
                ? null
                : new RecapGridAgentControlProfileRegistry([agentProfile]);

        EventAddress expectedHead = recovery.CapturedHead
            ?? throw new InvalidDataException(
                "The supported online phase has no captured raw head."
            );
        string connectionsPath = options.RequireSingle("connections");
        CliIo.EnsurePathChainHasNoReparsePoint(
            connectionsPath,
            "--connections"
        );
        CompletionConnectionsFileConfig connections =
            RecapGridCompletionConnectionsManifest.Decode(ReadBoundedFile(
                connectionsPath,
                RecapGridCompletionConnectionsLimits.MaximumInputUtf8Bytes
            ));
        string? routesPath = options.GetOptionalSingle("routes");
        if (mode is CandidateOnlineMode.SendNewTurn
                or CandidateOnlineMode.CompleteObservation
                or CandidateOnlineMode.CompleteToolResult
                or CandidateOnlineMode.ResumeTool
            && routesPath is null) {
            throw new ArgumentException(
                "--routes is required when starting a new completion request."
            );
        }
        if (routesPath is not null) {
            CliIo.EnsurePathChainHasNoReparsePoint(routesPath, "--routes");
        }

        await using RecapGridCompletionHost completionHost =
            agentProfiles is null
                ? RecapGridCompletionHost.Create(
                () => RecapGridRouteManifest.DecodeCanonical(
                    ReadBoundedFile(
                        routesPath ?? throw new InvalidOperationException(
                            "Prepared recovery must not resolve recap routes."
                        ),
                        RecapGridRouteManifestLimits.MaximumCanonicalUtf8Bytes
                    )
                ),
                connections,
                completionClientFactory
            )
                : RecapGridCompletionHost.Create(
                    () => RecapGridRouteManifest.DecodeCanonical(
                        ReadBoundedFile(
                            routesPath ?? throw new InvalidOperationException(
                                "Prepared recovery must not resolve recap routes."
                            ),
                            RecapGridRouteManifestLimits
                                .MaximumCanonicalUtf8Bytes
                        )
                    ),
                    connections,
                    completionClientFactory,
                    agentProfiles
                );

        CompletionConnectionConfig connection;
        ICompletionClient agentClient;
        CompletionDispatchIdentity dispatchIdentity;
        RecapGridOnlineContextHandle? online = null;
        RecapGridAgentControlHandle? agentControl = null;
        try {
            if (recovery is SessionRuntimeRecoveryRequirements
                    .FrozenCompletionRequired frozen) {
                string? requested = options.GetOptionalSingle("connection");
                if (requested is not null
                    && !string.Equals(
                        requested,
                        frozen.CompletionTarget.ConnectionId,
                        StringComparison.Ordinal
                    )) {
                    return Print(
                        "run-online-turn",
                        "prepared-binding-mismatch",
                        new {
                            required = frozen.CompletionTarget.ConnectionId,
                            requested
                        },
                        exitCode: 2
                    );
                }
                dispatchIdentity = ToDispatchIdentity(frozen);
                CompletionDispatchBindingResult binding =
                    completionHost.BindPreparedExact(dispatchIdentity);
                if (binding is CompletionDispatchBindingResult.Unavailable
                        unavailable) {
                    return Print(
                        "run-online-turn",
                        "prepared-binding-unavailable",
                        new {
                            reason = unavailable.Reason,
                            unavailable.Detail
                        },
                        exitCode: 2
                    );
                }
                var bound = binding as CompletionDispatchBindingResult.Bound
                    ?? throw new InvalidDataException(
                        "Completion exact binding returned an unknown result."
                    );
                connection = bound.Connection;
                agentClient = bound.Client;
                if (frozen.ToolRuntimeIdentity is { } toolIdentity) {
                    RecapGridAgentControlOpenResult toolBinding =
                        completionHost.BindAgentControlExact(
                            engine.ReadView,
                            toolIdentity,
                            new O200kBaseHistoryUnitLoadEstimator()
                        );
                    if (toolBinding is not RecapGridAgentControlOpenResult
                            .Opened toolOpened) {
                        return MapAgentControlBinding(toolBinding);
                    }
                    agentControl = toolOpened.Handle;
                    if (!string.Equals(
                            SessionVisibleToolSetFingerprint.ComputeSha256(
                                agentControl.ToolSession.VisibleDefinitions
                            ),
                            frozen.VisibleToolSetSha256,
                            StringComparison.Ordinal
                        )) {
                        return Print(
                            "run-online-turn",
                            "tool-set-fingerprint-mismatch",
                            new { nextAction = "inspect" },
                            exitCode: 2
                        );
                    }
                }
            }
            else {
                if (recovery is SessionRuntimeRecoveryRequirements
                        .ToolContinuationRequired toolContinuation) {
                    RecapGridAgentControlOpenResult frozenTool =
                        completionHost.BindAgentControlExact(
                            engine.ReadView,
                            toolContinuation.ToolRuntimeIdentity,
                            new O200kBaseHistoryUnitLoadEstimator()
                        );
                    if (frozenTool is not RecapGridAgentControlOpenResult
                            .Opened frozenOpened) {
                        return MapAgentControlBinding(frozenTool);
                    }
                    agentControl = frozenOpened.Handle;
                }
                string requested = options.RequireSingle("connection");
                RecapGridAgentConnectionResult agent =
                    completionHost.BindAgentExact(requested);
                if (agent is not RecapGridAgentConnectionResult.Bound bound) {
                    return MapAgentBinding(agent);
                }
                connection = bound.Connection;
                agentClient = bound.Client;
                dispatchIdentity = bound.Identity;

                if (agentControl is null) {
                    RecapGridAgentControlOpenResult currentTool =
                        completionHost.OpenAgentControl(
                            engine.ReadView,
                            agentProfile!.ProfileId,
                            new O200kBaseHistoryUnitLoadEstimator()
                        );
                    if (currentTool is not RecapGridAgentControlOpenResult
                            .Opened currentOpened) {
                        return MapAgentControlBinding(currentTool);
                    }
                    agentControl = currentOpened.Handle;
                }

                if (mode == CandidateOnlineMode.SendNewTurn) {
                    expectedHead = ReconcileOnlineSetup(
                        engine,
                        recovery,
                        connection
                    ).Head;
                }
                else {
                    ValidateOnlineSetup(engine, recovery, connection);
                }

                RecapGridOnlineOpenResult opened = RecapGridOnlineFactory.Open(
                    engine,
                    completionHost.Executor,
                    RecapGridOnlineLimits.Production,
                    new O200kBaseHistoryUnitLoadEstimator()
                );
                if (opened is not RecapGridOnlineOpenResult.Opened available) {
                    return MapOnlineOpen(opened);
                }
                online = available.Handle;
            }

            engine.UseRuntime(new SessionRuntime(
                agentClient,
                agentControl?.ToolSession,
                CompletionTarget:
                    CompletionTargetIdentityFactory.Create(dispatchIdentity),
                MaxTokens: connection.MaxTokens,
                UncertainCompletionRecoveryPolicy: recoveryPolicy,
                ToolRuntimeIdentity: agentControl?.RuntimeIdentity,
                ContextCandidateSource: online?.CandidateSource,
                MaximumCanonicalRequestBytes: ParsePositiveOnlineLong(
                    options.GetOptionalSingle(
                        "maximum-canonical-request-bytes"
                    )
                ),
                ContextLifecycle: online?.Lifecycle
            ));

            CompletionDescriptor invocation;
            IReadOnlyList<string>? errors;
            if (mode == CandidateOnlineMode.SendNewTurn) {
                TurnResult result = await engine.SendAsync(
                    expectedHead,
                    message!,
                    CancellationToken.None
                ).ConfigureAwait(false);
                invocation = result.Invocation;
                errors = result.Errors;
            }
            else {
                ResumeOutcome result = await engine.ResumeAsync(
                    expectedHead,
                    CancellationToken.None
                ).ConfigureAwait(false);
                if (!result.Advanced
                    || result.Invocation is null
                    || result.Message is null) {
                    return Print(
                        "run-online-turn",
                        "not-advanced",
                        new { phase = recovery.Phase.ToString() },
                        exitCode: 2
                    );
                }
                invocation = result.Invocation;
                errors = result.Errors;
            }

            SessionExecutionBoundaryInspection final =
                engine.InspectExecutionBoundary();
            return Print(
                "run-online-turn",
                "completed",
                new {
                    branch = engine.BranchName,
                    refId = engine.BranchRefId.ToHexString(),
                    head = FormatAddress(final.Head),
                    phase = final.Phase.ToString(),
                    invocation.ProviderId,
                    invocation.ApiSpecId,
                    invocation.Model,
                    errorCount = errors?.Count ?? 0,
                    recapEvidence = completionHost.Telemetry.ReadSnapshot()
                }
            );
        }
        catch (SessionSelectedLineageAuditChangedException changed) {
            return Print(
                "run-online-turn",
                "raw-head-changed",
                new {
                    kind = changed.Kind.ToString(),
                    expected = FormatAddress(changed.ExpectedHead),
                    observed = FormatAddress(changed.ObservedHead),
                    nextAction = "inspect"
                },
                exitCode: 2
            );
        }
        catch (SessionJournalNotReadyException unavailable) {
            return Print(
                "run-online-turn",
                "not-ready",
                new {
                    reason = unavailable.Reason.ToString(),
                    nextAction = "inspect"
                },
                exitCode: 2
            );
        }
        finally {
            if (online is not null) {
                await online.DisposeAsync().ConfigureAwait(false);
            }
            agentControl?.Dispose();
        }
    }

    private static CandidateOnlineMode ClassifyOnlineMode(
        SessionRuntimeRecoveryRequirements value
    ) => value switch {
        SessionRuntimeRecoveryRequirements.NoRuntimeRequired
            when value.Phase == SessionExecutionPhase.Idle
            => CandidateOnlineMode.SendNewTurn,
        SessionRuntimeRecoveryRequirements.NewRequestRequired
            when value.HeadKind == SessionEventKind.ObservationAccepted
            => CandidateOnlineMode.CompleteObservation,
        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired {
            DispatchState: SessionDurableDispatchState.NotStarted
        } => CandidateOnlineMode.ResumePrepared,
        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired {
            DispatchState: SessionDurableDispatchState.StartedOutcomeUncertain
        } => CandidateOnlineMode.ResumeStarted,
        SessionRuntimeRecoveryRequirements.NoRuntimeRequired
            when value.Phase == SessionExecutionPhase.Empty
            => throw new InvalidOperationException(
                "run-online-turn requires an initialized SessionJournal."
            ),
        SessionRuntimeRecoveryRequirements.NewRequestRequired
            when value.HeadKind == SessionEventKind.ToolResultObserved
            => CandidateOnlineMode.CompleteToolResult,
        SessionRuntimeRecoveryRequirements.ToolContinuationRequired
            => CandidateOnlineMode.ResumeTool,
        SessionRuntimeRecoveryRequirements.FailedTurnMustBeAbandoned
            => throw new InvalidOperationException(
                "The failed turn must be abandoned before a new request."
            ),
        _ => throw new InvalidOperationException(
            $"Unsupported online phase '{value.Phase}'."
        )
    };

    private static void ValidateOnlineMessage(
        CandidateOnlineMode mode,
        string? message
    ) {
        if (mode == CandidateOnlineMode.SendNewTurn) {
            if (message is null) {
                throw new ArgumentException("--message is required for Idle.");
            }
        }
        else if (message is not null) {
            throw new ArgumentException(
                "--message must be absent when resuming an active turn."
            );
        }
    }

    private static SessionUncertainCompletionRecoveryPolicy
        ParseOnlineRecoveryPolicy(string? value) => value switch {
            null or "refuse"
                => SessionUncertainCompletionRecoveryPolicy.Refuse,
            "restart-new-attempt"
                => SessionUncertainCompletionRecoveryPolicy
                    .RestartWithNewAttempt,
            _ => throw new ArgumentException(
                "--uncertain-recovery must be refuse or restart-new-attempt."
            )
        };

    private static long? ParsePositiveOnlineLong(string? value) {
        if (value is null) { return null; }
        return long.TryParse(value, out long parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException(
                "--maximum-canonical-request-bytes must be positive."
            );
    }

    private static CompletionDispatchIdentity ToDispatchIdentity(
        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired frozen
    ) => new(
        frozen.CompletionTarget.ConnectionId,
        frozen.CompletionTarget.Kind,
        frozen.CompletionTarget.ConnectionFingerprint,
        frozen.ClientName,
        frozen.ApiSpecId,
        frozen.CompletionTarget.RequestAdapterFingerprint
    );

    private static SessionGoverningSetup ReconcileOnlineSetup(
        SessionJournalEngine engine,
        SessionRuntimeRecoveryRequirements requirement,
        CompletionConnectionConfig connection
    ) {
        EventAddress head = requirement.CapturedHead
            ?? throw new InvalidDataException("Setup reconciliation has no head.");
        SessionGoverningSetup current = engine.ResolveGoverningSetup(head);
        SessionDesiredSetupReconciliationResult result =
            engine.ReconcileDesiredSetup(
                head,
                new SessionDesiredSetup(
                    connection.ModelId,
                    connection.CompletionSurfaceId,
                    current.SystemPrompt
                )
            );
        return result switch {
            SessionDesiredSetupReconciliationResult.Ready ready
                => ready.GoverningSetup,
            SessionDesiredSetupReconciliationResult.Unavailable unavailable
                => throw new InvalidOperationException(
                    $"Desired setup is unavailable ({unavailable.Reason})."
                ),
            SessionDesiredSetupReconciliationResult.Retryable retryable
                => throw new InvalidOperationException(
                    $"Raw head changed from {retryable.ExpectedHead} to {retryable.ObservedHead}."
                ),
            _ => throw new InvalidDataException(
                "Unknown desired setup reconciliation result."
            )
        };
    }

    private static void ValidateOnlineSetup(
        SessionJournalEngine engine,
        SessionRuntimeRecoveryRequirements requirement,
        CompletionConnectionConfig connection
    ) {
        EventAddress head = requirement.CapturedHead
            ?? throw new InvalidDataException("Active request has no head.");
        SessionGoverningSetup setup = engine.ResolveGoverningSetup(head);
        if (!string.Equals(
                setup.RuntimeConfig.ModelId,
                connection.ModelId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                setup.RuntimeConfig.CompletionSurfaceId,
                connection.CompletionSurfaceId,
                StringComparison.Ordinal
            )) {
            throw new InvalidOperationException(
                "The exact connection does not match the active governing setup."
            );
        }
    }

    private static int MapAgentBinding(RecapGridAgentConnectionResult result)
        => result switch {
            RecapGridAgentConnectionResult.Absent absent => Print(
                "run-online-turn",
                "connection-absent",
                new { absent.ConnectionId },
                exitCode: 2
            ),
            RecapGridAgentConnectionResult.Invalid invalid => Print(
                "run-online-turn",
                "connection-invalid",
                new { invalid.Code, invalid.Detail },
                exitCode: 2
            ),
            _ => throw new InvalidDataException(
                "Unknown exact agent binding outcome."
            )
        };

    private static int MapOnlineOpen(RecapGridOnlineOpenResult result)
        => result switch {
            RecapGridOnlineOpenResult.Absent absent => Print(
                "run-online-turn",
                "derived-state-absent",
                new { component = absent.Component.ToString() },
                exitCode: 2
            ),
            RecapGridOnlineOpenResult.Busy busy => Print(
                "run-online-turn",
                "busy",
                new { component = busy.Component.ToString() },
                exitCode: 2
            ),
            RecapGridOnlineOpenResult.UnsupportedSchema unsupported => Print(
                "run-online-turn",
                "unsupported-schema",
                new {
                    component = unsupported.Component.ToString(),
                    unsupported.SchemaVersion
                },
                exitCode: 2
            ),
            RecapGridOnlineOpenResult.DisposedRawAuthority => Print(
                "run-online-turn",
                "disposed",
                new { component = "raw-authority" },
                exitCode: 2
            ),
            RecapGridOnlineOpenResult.Invalid invalid => Print(
                "run-online-turn",
                "invalid",
                new {
                    component = invalid.Component.ToString(),
                    invalid.Code,
                    invalid.Detail
                },
                exitCode: 2
            ),
            _ => throw new InvalidDataException(
                "Unknown online open outcome."
            )
        };

    private static int MapAgentControlBinding(
        RecapGridAgentControlOpenResult result
    ) => result switch {
        RecapGridAgentControlOpenResult.ProfileAbsent value => Print(
            "run-online-turn",
            "tool-runtime-profile-absent",
            new { value.ProfileId, nextAction = "inspect" },
            exitCode: 2
        ),
        RecapGridAgentControlOpenResult.Busy value => Print(
            "run-online-turn",
            "busy",
            new { component = value.Component },
            exitCode: 2
        ),
        RecapGridAgentControlOpenResult.UnsupportedSchema value => Print(
            "run-online-turn",
            "unsupported-schema",
            new { component = value.Component, value.SchemaVersion },
            exitCode: 2
        ),
        RecapGridAgentControlOpenResult.ControlAbsent => Print(
            "run-online-turn",
            "derived-state-absent",
            new { component = "control" },
            exitCode: 2
        ),
        RecapGridAgentControlOpenResult.TimelineAbsent => Print(
            "run-online-turn",
            "derived-state-absent",
            new { component = "timeline" },
            exitCode: 2
        ),
        RecapGridAgentControlOpenResult.Invalid value => Print(
            "run-online-turn",
            "invalid",
            new { value.Component, value.Code, value.Detail },
            exitCode: 2
        ),
        _ => throw new InvalidDataException(
            "Unknown Agent Control binding outcome."
        )
    };

    private static string? FormatAddress(EventAddress? value)
        => value is { } address
            ? EventAddressTextCodec.Format(address)
            : null;

    private enum CandidateOnlineMode {
        SendNewTurn,
        CompleteObservation,
        CompleteToolResult,
        ResumePrepared,
        ResumeStarted,
        ResumeTool
    }
}
