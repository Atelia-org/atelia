using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Online;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    internal static async ValueTask<int> RunOnlineTurnAsync(
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
            "producer-target"
        );
        BuildTarget? producerTarget = options.GetOptionalSingle(
            "producer-target"
        ) is { } producerTargetPath
            ? BuildTarget.DecodeCanonical(ReadBoundedFile(
                producerTargetPath, MaximumInputUtf8Bytes))
            : null;
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
        if (recovery is SessionRuntimeRecoveryRequirements
                .FrozenCompletionRequired { ToolRuntimeIdentity: not null }
            or SessionRuntimeRecoveryRequirements.ToolContinuationRequired) {
            return Print(
                "run-online-turn",
                "tool-runtime-unsupported",
                new { nextAction = "inspect" },
                exitCode: 2
            );
        }
        RecapGridOnlineMode mode = ClassifyOnlineMode(recovery);
        string? message = options.GetOptionalSingle("message");
        ValidateOnlineMessage(mode, message);

        EventAddress expectedHead = recovery.CapturedHead
            ?? throw new InvalidDataException(
                "The supported online phase has no captured raw head."
            );
        SessionInputContent systemInstructions = engine.ResolveGoverningSetup(expectedHead).SystemPrompt;
        if (systemInstructions.IsStructured) {
            // The generic CLI has no domain projector. Do not replace the
            // structured setup with raw JSON or a guessed text prompt.
            return Print(
                "run-online-turn",
                "unsupported-input-schema",
                new { schemaId = systemInstructions.SchemaId, nextAction = "use-the-owning-host" },
                exitCode: 2
            );
        }
        string connectionsPath = options.RequireSingle("connections");
        CliIo.EnsurePathChainHasNoReparsePoint(
            connectionsPath,
            "--connections"
        );
        CompletionConnectionsFileConfig connections =
            CompletionConnectionConfigLoader.Decode(ReadBoundedFile(
                connectionsPath,
                CompletionConnectionConfigLoader.MaximumInputUtf8Bytes
            ));
        string? routesPath = options.GetOptionalSingle("routes");
        if (mode is RecapGridOnlineMode.SendNewTurn
                or RecapGridOnlineMode.CompleteObservation
                or RecapGridOnlineMode.CompleteToolResult
            && routesPath is null) {
            throw new ArgumentException(
                "--routes is required when starting a new completion request."
            );
        }
        if (routesPath is not null) {
            CliIo.EnsurePathChainHasNoReparsePoint(routesPath, "--routes");
        }

        await using RecapGridCompletionHost completionHost =
            RecapGridCompletionHost.Create(
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
            );

        CompletionConnectionConfig connection;
        ICompletionClient agentClient;
        CompletionDispatchIdentity dispatchIdentity;
        RecapGridOnlineContextHandle? online = null;
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
            }
            else {
                string requested = options.RequireSingle("connection");
                RecapGridAgentConnectionLookupResult inspected =
                    completionHost.InspectAgentExact(requested);
                if (inspected is not RecapGridAgentConnectionLookupResult
                        .Found found) {
                    return inspected switch {
                        RecapGridAgentConnectionLookupResult.Absent value
                            => Print(
                                "run-online-turn",
                                "connection-absent",
                                new { value.ConnectionId },
                                2),
                        RecapGridAgentConnectionLookupResult.Invalid value
                            => Print(
                                "run-online-turn",
                                "connection-invalid",
                                new { value.Code, value.Detail },
                                2),
                        _ => Print(
                            "run-online-turn",
                            "connection-invalid",
                            exitCode: 2)
                    };
                }
                connection = found.Connection;

                if (mode == RecapGridOnlineMode.SendNewTurn) {
                    expectedHead = ReconcileOnlineSetup(
                        engine,
                        recovery,
                        connection
                    ).Head;
                }
                else {
                    ValidateOnlineSetup(engine, expectedHead, connection);
                }

                RecapGridOnlineOpenResult opened = producerTarget is null
                    ? RecapGridOnlineFactory.Open(
                        engine,
                        completionHost.Executor,
                        RecapGridOnlineLimits.Production,
                        new O200kBaseHistoryUnitLoadEstimator()
                    )
                    : RecapGridOnlineFactory.Open(
                        engine,
                        completionHost.Executor,
                        producerTarget,
                        RecapGridOnlineLimits.Production,
                        new O200kBaseHistoryUnitLoadEstimator()
                    );
                if (opened is not RecapGridOnlineOpenResult.Opened available) {
                    return MapOnlineOpen(opened);
                }
                online = available.Handle;

                RecapGridOnlinePassResult caughtUp = await online
                    .CatchUpMaintenanceAsync(
                        mode == RecapGridOnlineMode.SendNewTurn
                            ? SessionInputContent.Text(message!)
                            : null,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (caughtUp is not RecapGridOnlinePassResult.Ready
                        and not RecapGridOnlinePassResult
                            .RawHistoryAuthorized) {
                    return PrintOnlineCatchUp(caughtUp);
                }

                RecapGridAgentConnectionResult agent =
                    completionHost.BindAgentExact(requested);
                if (agent is not RecapGridAgentConnectionResult.Bound bound) {
                    return MapAgentBinding(agent);
                }
                agentClient = bound.Client;
                dispatchIdentity = bound.Identity;
            }

            engine.UseRuntime(new SessionRuntime(
                agentClient,
                CompletionTarget:
                    CompletionTargetIdentityFactory.Create(dispatchIdentity),
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
            if (mode == RecapGridOnlineMode.SendNewTurn) {
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
                    recapEvidence = completionHost.ReadTelemetrySnapshot()
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
        }
    }

    private static RecapGridOnlineMode ClassifyOnlineMode(
        SessionRuntimeRecoveryRequirements value
    ) => value switch {
        SessionRuntimeRecoveryRequirements.NoRuntimeRequired
            when value.Phase == SessionExecutionPhase.Idle
            => RecapGridOnlineMode.SendNewTurn,
        SessionRuntimeRecoveryRequirements.NewRequestRequired
            when value.HeadKind == SessionEventKind.ObservationAccepted
            => RecapGridOnlineMode.CompleteObservation,
        SessionRuntimeRecoveryRequirements.FrozenCompletionRequired
            => RecapGridOnlineMode.ResumePrepared,
        SessionRuntimeRecoveryRequirements.NoRuntimeRequired
            when value.Phase == SessionExecutionPhase.Empty
            => throw new InvalidOperationException(
                "run-online-turn requires an initialized SessionJournal."
            ),
        SessionRuntimeRecoveryRequirements.NewRequestRequired
            when value.HeadKind == SessionEventKind.ToolResultObserved
            => RecapGridOnlineMode.CompleteToolResult,
        SessionRuntimeRecoveryRequirements.LegacyFailedTurnBlocked
            => throw new InvalidOperationException(
                "The historical failed turn requires explicit closure before a new request."
            ),
        _ => throw new InvalidOperationException(
            $"Unsupported online phase '{value.Phase}'."
        )
    };

    private static void ValidateOnlineMessage(
        RecapGridOnlineMode mode,
        string? message
    ) {
        if (mode == RecapGridOnlineMode.SendNewTurn) {
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
        frozen.ApiSpecId
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
        EventAddress head,
        CompletionConnectionConfig connection
    ) {
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

    private static int PrintOnlineCatchUp(
        RecapGridOnlinePassResult result
    ) => result switch {
        RecapGridOnlinePassResult.MaintenanceContinuation value => Print(
            "run-online-turn",
            "maintenance-continuation",
            new {
                component = value.Component.ToString(),
                value.Code,
                value.Detail,
                value.Evidence
            },
            exitCode: 2),
        RecapGridOnlinePassResult.Backpressure value => Print(
            "run-online-turn",
            "maintenance-backpressure",
            new {
                component = value.Component.ToString(),
                value.Code,
                value.Detail,
                value.MaintenanceEvidence
            },
            exitCode: 2),
        RecapGridOnlinePassResult.Unavailable value => Print(
            "run-online-turn",
            "maintenance-unavailable",
            new {
                component = value.Component.ToString(),
                value.Code,
                value.Detail,
                value.MaintenanceEvidence
            },
            exitCode: 2),
        RecapGridOnlinePassResult.Disposed => Print(
            "run-online-turn", "disposed", exitCode: 2),
        _ => throw new InvalidDataException(
            "Unknown Online catch-up outcome.")
    };

    private static string? FormatAddress(EventAddress? value)
        => value is { } address
            ? EventAddressTextCodec.Format(address)
            : null;

    private enum RecapGridOnlineMode {
        SendNewTurn,
        CompleteObservation,
        CompleteToolResult,
        ResumePrepared
    }
}
