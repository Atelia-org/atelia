using Atelia.EventJournal;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

/// <summary>
/// Restores one exact Published recap set without changing its frozen plan or
/// membership. Building Resume and online lifecycle remain separate flows.
/// </summary>
public sealed class DerivedRecapRestoreExecutor {
    private readonly SessionJournalEngine _engine;
    private readonly DerivedRecapStore _store;
    private readonly RecapProtocolHardCaps _hardCaps;
    private readonly IRecapBlockMaintainerRegistry _maintainers;
    private readonly DerivedRecapRestorer _restorer;

    public DerivedRecapRestoreExecutor(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        IRecapBlockMaintainerRegistry maintainers
    ) : this(
        engine,
        store,
        maintainers,
        RecapProtocolHardCaps.V4
    ) {
    }

    internal DerivedRecapRestoreExecutor(
        SessionJournalEngine engine,
        DerivedRecapStore store,
        IRecapBlockMaintainerRegistry maintainers,
        RecapProtocolHardCaps hardCaps
    ) {
        _engine = engine
            ?? throw new ArgumentNullException(nameof(engine));
        _store = store
            ?? throw new ArgumentNullException(nameof(store));
        _maintainers = maintainers
            ?? throw new ArgumentNullException(nameof(maintainers));
        _hardCaps = hardCaps
            ?? throw new ArgumentNullException(nameof(hardCaps));
        _restorer = new DerivedRecapRestorer(store, engine);
    }

    public async ValueTask<DerivedRecapRestoreResult> RestoreAsync(
        EventAddress setAdmissionAnchor,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken = default
    ) {
        if (setAdmissionAnchor == default) {
            throw new ArgumentException(
                "SetAdmissionAnchor cannot be default.",
                nameof(setAdmissionAnchor)
            );
        }
        if (expectedRawHead == default) {
            throw new ArgumentException(
                "Expected raw head cannot be default.",
                nameof(expectedRawHead)
            );
        }

        try {
            DerivedRecapLineageView lineageView =
                DerivedRecapLineageView.Capture(
                    _store,
                    _engine,
                    cancellationToken
                );
            SessionCurrentLineagePrefix lineage = lineageView.Prefix;
            if (lineageView.CapturedHead != expectedRawHead) {
                return RawHeadChanged(
                    expectedRawHead,
                    lineageView.CapturedHead
                );
            }

            PublishedPlanAtAnchorReadResult planRead =
                await _store.ReadPublishedPlanAtAnchorAsync(
                        setAdmissionAnchor,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            DerivedRecapSetManifest frozenPlan;
            PublishedRestorePlanAuthority restorePlanAuthority;
            IReadOnlyList<RecapBlockCommitment> commitments;
            switch (planRead) {
                case PublishedPlanAtAnchorReadResult.Available plan:
                    frozenPlan = plan.Snapshot.FrozenPlan;
                    restorePlanAuthority = plan.Authority;
                    commitments = plan.Snapshot.BlockCommitments;
                    break;
                case PublishedPlanAtAnchorReadResult
                    .ManifestWitnessAvailable witness:
                    frozenPlan = witness.FrozenPlan;
                    restorePlanAuthority = witness.Authority;
                    commitments = witness.Authority.BlockCommitments;
                    break;
                default:
                    return planRead switch {
                    PublishedPlanAtAnchorReadResult.Missing =>
                        Unavailable(
                            DerivedRecapRestoreDefectCodes.StoreUnavailable,
                            "Published metadata is missing."
                        ),
                    PublishedPlanAtAnchorReadResult.Unavailable unavailable =>
                        Unavailable(unavailable.Defects),
                    PublishedPlanAtAnchorReadResult.Changed =>
                        new DerivedRecapRestoreResult.Retryable(
                            DerivedRecapRestoreDefectCodes
                                .ConcurrentPublishedChange,
                            "Published metadata changed during restore preflight."
                        ),
                    _ => throw new InvalidOperationException(
                        "Unknown Published plan read result."
                    )
                    };
            }
            foreach (RecapBlockCommitment commitment in commitments) {
                switch (lineage.Lookup(commitment.AbsorbedThrough)) {
                    case SessionCurrentLineageAnchorLookup.Found:
                        break;
                    case SessionCurrentLineageAnchorLookup
                        .BeyondPrefix commitmentBeyond:
                        return new DerivedRecapRestoreResult.BeyondPrefix(
                            DerivedRecapBeyondPrefixStage
                                .RestorePendingWindow,
                            commitmentBeyond.Evidence
                        );
                    case SessionCurrentLineageAnchorLookup.OffLineage:
                        return Unavailable(
                            DerivedRecapRestoreDefectCodes
                                .FrozenPlanInvalid,
                            $"Published commitment for block "
                            + $"'{commitment.RecapBlockId}' is outside "
                            + "the captured raw lineage."
                        );
                    default:
                        throw new InvalidDataException(
                            "Unknown commitment lineage lookup result."
                        );
                }
            }
            RecapFrozenPlanBarrierResult restorePreflight =
                await RecapFrozenPlanBarrier.ProveAsync(
                        _engine,
                        _store,
                        frozenPlan,
                        lineage,
                        expectedRawHead,
                        _hardCaps,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (restorePreflight.BeyondPrefix is { } preflightBeyond) {
                return new DerivedRecapRestoreResult.BeyondPrefix(
                    DerivedRecapBeyondPrefixStage.RestorePendingWindow,
                    preflightBeyond
                );
            }
            if (restorePreflight.Defects.Count != 0) {
                return new DerivedRecapRestoreResult.Unavailable([
                    .. restorePreflight.Defects.Select(defect =>
                        new DerivedRecapRestoreDefect(
                            defect.Kind switch {
                                RecapFrozenPlanBarrierDefectKind
                                    .ExecutionLimit =>
                                    DerivedRecapRestoreDefectCodes
                                        .ExecutionLimitExceeded,
                                RecapFrozenPlanBarrierDefectKind
                                    .StoreUnavailable =>
                                    DerivedRecapRestoreDefectCodes
                                        .StoreUnavailable,
                                _ => DerivedRecapRestoreDefectCodes
                                    .FrozenPlanInvalid
                            },
                            defect.Detail
                        ))
                ]);
            }

            PublishedRestoreInspectionResult inspectionResult =
                await lineageView.InspectPublishedForRestoreAsync(
                        restorePlanAuthority,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            if (inspectionResult
                    is PublishedRestoreInspectionResult.Unavailable
                        inspectionUnavailable) {
                if (inspectionUnavailable.Defects.Any(
                        static defect => defect.Code
                            == "ConcurrentPublishedChange"
                    )) {
                    return new DerivedRecapRestoreResult.Retryable(
                        DerivedRecapRestoreDefectCodes
                            .ConcurrentPublishedChange,
                        "Published metadata changed between restore "
                        + "preflight and component inspection."
                    );
                }
                return Unavailable(inspectionUnavailable.Defects);
            }
            if (inspectionResult
                is PublishedRestoreInspectionResult.BeyondPrefix beyond) {
                return Unavailable(
                    DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
                    "Exact Published component inspection contradicted "
                    + "the completed Restore metadata proof at anchor "
                    + $"'{beyond.Evidence.RequiredAnchor}'."
                );
            }
            PublishedRestoreInspection inspection =
                ((PublishedRestoreInspectionResult.Available)
                    inspectionResult).Inspection;
            PreparedRestore prepared = Prepare(
                inspection,
                lineage,
                expectedRawHead,
                cancellationToken
            );
            if (prepared.Defects.Count != 0) {
                return new DerivedRecapRestoreResult.Unavailable(
                    prepared.Defects
                );
            }
            if (prepared.BeyondPrefix is { } pendingBeyond) {
                return new DerivedRecapRestoreResult.BeyondPrefix(
                    DerivedRecapBeyondPrefixStage.RestorePendingWindow,
                    pendingBeyond
                );
            }

            EventAddress? observedHead = _engine.ReadCurrentHead();
            if (observedHead != expectedRawHead) {
                return RawHeadChanged(expectedRawHead, observedHead);
            }

            var finalAuthorities =
                new List<PublishedBlockWriteAuthority>(
                    prepared.Actions.Count
                );
            foreach (RestoreBlockAction action in prepared.Actions) {
                RestoreBlockExecution execution =
                    await ExecuteBlockAsync(
                            inspection.Handle,
                            action,
                            prepared.Windows,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                if (execution.Failure is { } failure) {
                    return failure;
                }
                finalAuthorities.Add(execution.WriteAuthority!);
            }

            PublishedEnvelopeCommitAuthority commitAuthority =
                _store.IssuePublishedEnvelopeCommitAuthority(
                    inspection.Handle,
                    finalAuthorities
                );
            PublishedEnvelopeCommitResult commit =
                await _restorer.CommitEnvelopeAsync(
                        commitAuthority,
                        expectedRawHead,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            return commit switch {
                PublishedEnvelopeCommitResult.Committed committed =>
                    new DerivedRecapRestoreResult.Restored(
                        committed.Descriptor
                    ),
                PublishedEnvelopeCommitResult.AlreadyCommitted current =>
                    new DerivedRecapRestoreResult.Restored(
                        current.Descriptor
                    ),
                PublishedEnvelopeCommitResult.Stale stale =>
                    new DerivedRecapRestoreResult.Retryable(
                        stale.Code,
                        stale.Detail
                    ),
                PublishedEnvelopeCommitResult.Unavailable unavailable =>
                    Unavailable(unavailable.Defects),
                _ => throw new InvalidOperationException(
                    "Unknown Published envelope commit result."
                )
            };
        }
        catch (RecapRawHeadChangedException changed) {
            return RawHeadChanged(changed.Expected, changed.Observed);
        }
        catch (Exception exception)
            when (IsAvailabilityException(exception)) {
            return Unavailable(
                DerivedRecapRestoreDefectCodes.StoreUnavailable,
                exception.Message
            );
        }
    }

    private PreparedRestore Prepare(
        PublishedRestoreInspection inspection,
        SessionCurrentLineagePrefix lineage,
        EventAddress expectedRawHead,
        CancellationToken cancellationToken
    ) {
        var defects = new List<DerivedRecapRestoreDefect>();
        var actions = new List<RestoreBlockAction>(
            inspection.FrozenPlan.Blocks.Count
        );
        if (inspection.FrozenPlan.Blocks.Count
            > _hardCaps.MaxCatalogEntries
            || inspection.FrozenPlan.Blocks.Any(plan =>
                plan.MaxContentUtf8Bytes
                    > _hardCaps.MaxContentUtf8Bytes)) {
            AddDefect(
                defects,
                DerivedRecapRestoreDefectCodes.ExecutionLimitExceeded,
                "Frozen Published plan exceeds V4 catalog or content "
                + "protocol hard caps."
            );
        }
        foreach (RecapPendingWindowDefect defect
                 in RecapPendingWindowPreparer
                     .ValidateFrozenRouteLimits(
                         inspection.FrozenPlan.Blocks
                             .OfType<MaintainRecapBlockPlan>(),
                         _hardCaps
                     )) {
            AddDefect(
                defects,
                DerivedRecapRestoreDefectCodes.ExecutionLimitExceeded,
                defect.Detail
            );
        }
        if (defects.Count != 0) {
            return PreparedRestore.Unavailable(defects);
        }
        Dictionary<RecapBlockId, DerivedRecapFrozenInput>
            healthyInputs = inspection.Blocks
                .Where(static item =>
                    item.Value.FrozenInput
                        is FrozenRecapInputHealth.Healthy)
                .ToDictionary(
                    static item => item.Key,
                    static item =>
                        ((FrozenRecapInputHealth.Healthy)
                            item.Value.FrozenInput).Input
                );

        foreach (RecapBlockPlan plan
                 in inspection.FrozenPlan.Blocks) {
            if (!inspection.Blocks.TryGetValue(
                    plan.RecapBlockId,
                    out PublishedBlockRestoreInspection? block
                )) {
                AddDefect(
                    defects,
                    DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
                    $"Block '{plan.RecapBlockId}' has no exact "
                    + "restore inspection."
                );
                continue;
            }
            if (!string.Equals(
                    DerivedRecapCodec.ComputeBlockPlanSha256(plan),
                    DerivedRecapCodec.ComputeBlockPlanSha256(
                        block.Plan
                    ),
                    StringComparison.Ordinal
                )) {
                AddDefect(
                    defects,
                    DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
                    $"Block '{plan.RecapBlockId}' inspection differs "
                    + "from the frozen plan."
                );
                continue;
            }

            // A missing disposable Existing-source input does not make an
            // already committed or checkpoint-resumable block unavailable.
            // The frozen barrier already authenticated every setup proof
            // before Published components were read; do not re-walk that
            // authority after entering the component phase.
            if (plan is MaintainRecapBlockPlan {
                        Source: EmptyRecapMaintainSource
                    }
                    || healthyInputs.ContainsKey(plan.RecapBlockId)) {
                foreach (RecapFrozenPlanRawDefect defect
                         in RecapFrozenPlanRawValidator
                             .ValidateInputDependentBlock(
                             _engine,
                             inspection.FrozenPlan,
                             healthyInputs,
                             lineage,
                             plan
                         )) {
                    AddDefect(
                        defects,
                        DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
                        defect.Detail
                    );
                }
            }

            RestoreBlockAction? action = CreateAction(
                plan,
                block,
                defects
            );
            if (action is not null) {
                actions.Add(action);
            }
        }
        if (inspection.Blocks.Count
            != inspection.FrozenPlan.Blocks.Count) {
            AddDefect(
                defects,
                DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
                "Restore inspection block roster differs from the "
                + "exact frozen plan."
            );
        }
        if (defects.Count != 0) {
            return PreparedRestore.Unavailable(defects);
        }

        var pendingRoutes = new List<PendingMaintainRoute>();
        foreach (MaintainRestoreAction action
                 in actions.OfType<MaintainRestoreAction>()) {
            pendingRoutes.Add(new PendingMaintainRoute(
                action.MaintainPlan,
                action.StartExclusive,
                action.NextEndpointIndex
            ));
        }
        foreach (RecapPendingWindowDefect defect
                 in RecapPendingWindowPreparer
                     .ValidatePendingRouteLimits(
                         pendingRoutes,
                         _hardCaps
                     )) {
            AddDefect(
                defects,
                DerivedRecapRestoreDefectCodes.ExecutionLimitExceeded,
                defect.Detail
            );
        }
        if (defects.Count != 0) {
            return PreparedRestore.Unavailable(defects);
        }

        PreparedRecapPendingWindows exactWindows =
            RecapPendingWindowPreparer.Prepare(
                _engine,
                expectedRawHead,
                pendingRoutes,
                _hardCaps,
                cancellationToken
            );
        if (exactWindows.BeyondPrefix is not null) {
            AddDefect(
                defects,
                DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
                "Exact pending route contradicted its conservative preflight proof."
            );
        }
        foreach (RecapPendingWindowDefect defect
                 in exactWindows.Defects) {
            AddDefect(
                defects,
                DerivedRecapRestoreDefectCodes.ExecutionLimitExceeded,
                defect.Detail
            );
        }
        return new PreparedRestore(
            defects,
            actions,
            exactWindows.Windows
        );
    }

    private RestoreBlockAction? CreateAction(
        RecapBlockPlan plan,
        PublishedBlockRestoreInspection block,
        List<DerivedRecapRestoreDefect> defects
    ) {
        switch (block.Capability) {
            case PublishedBlockRestoreCapability.KeepCommitted:
                return HealthyNoOp(
                    plan,
                    block,
                    defects
                );
            case PublishedBlockRestoreCapability.AdoptPending:
                return HealthyNoOp(
                    plan,
                    block,
                    defects
                );
            case PublishedBlockRestoreCapability
                .InstallFinalCheckpoint:
                if (plan is not MaintainRecapBlockPlan
                    || block.Checkpoint
                        is not RollingRecapCheckpointHealth.Healthy
                            finalCheckpoint) {
                    AddInvalidCapability(defects, plan);
                    return null;
                }
                return new InstallFinalAction(
                    plan,
                    block.WriteAuthority,
                    finalCheckpoint.Block
                );
            case PublishedBlockRestoreCapability.ResumeSuffix suffix:
                if (plan is not MaintainRecapBlockPlan maintain
                    || block.Checkpoint
                        is not RollingRecapCheckpointHealth.Healthy
                            checkpoint
                    || suffix.NextEndpointIndex
                        != checkpoint.EndpointIndex + 1
                    || suffix.NextEndpointIndex
                        >= maintain.CatchUpBoundaries.Count) {
                    AddInvalidCapability(defects, plan);
                    return null;
                }
                return CreateMaintainAction(
                    maintain,
                    block,
                    checkpoint.Block,
                    suffix.NextEndpointIndex,
                    checkpoint.Block.AbsorbedThrough,
                    defects
                );
            case PublishedBlockRestoreCapability.ReplayBlock:
                if (plan is InheritRecapBlockPlan) {
                    if (block.FrozenInput
                            is not FrozenRecapInputHealth.Healthy
                                inherited) {
                        AddDependencyUnavailable(defects, plan);
                        return null;
                    }
                    return new InstallFinalAction(
                        plan,
                        block.WriteAuthority,
                        DerivedRecapCodec.CreateBlock(
                            plan,
                            inherited.Input.AbsorbedThrough,
                            inherited.Input.Content
                        )
                    );
                }
                if (plan is not MaintainRecapBlockPlan replay) {
                    AddInvalidCapability(defects, plan);
                    return null;
                }
                DerivedRecapBlock? initialBlock;
                EventAddress start;
                switch (replay.Source) {
                    case EmptyRecapMaintainSource empty:
                        initialBlock = null;
                        start = empty.ReplayStartExclusive;
                        break;
                    case ExistingRecapMaintainSource:
                        if (block.FrozenInput
                                is not FrozenRecapInputHealth.Healthy
                                    existing) {
                            AddDependencyUnavailable(defects, plan);
                            return null;
                        }
                        initialBlock = DerivedRecapCodec.CreateBlock(
                            plan,
                            existing.Input.AbsorbedThrough,
                            existing.Input.Content
                        );
                        start = existing.Input.AbsorbedThrough;
                        break;
                    default:
                        AddInvalidCapability(defects, plan);
                        return null;
                }
                return CreateMaintainAction(
                    replay,
                    block,
                    initialBlock,
                    nextEndpointIndex: 0,
                    start,
                    defects
                );
            case PublishedBlockRestoreCapability.Unavailable unavailable:
                foreach (RecapStructuralDefect defect
                         in unavailable.Defects) {
                    defects.Add(new DerivedRecapRestoreDefect(
                        defect.Code,
                        defect.Detail
                    ));
                }
                return null;
            default:
                AddInvalidCapability(defects, plan);
                return null;
        }
    }

    private RestoreBlockAction? CreateMaintainAction(
        MaintainRecapBlockPlan plan,
        PublishedBlockRestoreInspection block,
        DerivedRecapBlock? initialBlock,
        int nextEndpointIndex,
        EventAddress startExclusive,
        List<DerivedRecapRestoreDefect> defects
    ) {
        if (!_maintainers.TryResolve(
                plan.MaintainerId,
                plan.Target,
                plan.MaintainerCapabilityFingerprint,
                out IRecapBlockMaintainer? maintainer
            )
            || !string.Equals(
                maintainer.Id,
                plan.MaintainerId,
                StringComparison.Ordinal
            )
            || maintainer.Target != plan.Target
            || !string.Equals(
                maintainer.CapabilityFingerprint,
                plan.MaintainerCapabilityFingerprint,
                StringComparison.Ordinal
            )) {
            AddDefect(
                defects,
                DerivedRecapRestoreDefectCodes.MaintainerUnavailable,
                $"Maintainer binding for '{plan.RecapBlockId}' is "
                + "unavailable."
            );
            return null;
        }
        return new MaintainRestoreAction(
            plan,
            block.WriteAuthority,
            initialBlock,
            nextEndpointIndex,
            startExclusive,
            maintainer
        );
    }

    private static RestoreBlockAction? HealthyNoOp(
        RecapBlockPlan plan,
        PublishedBlockRestoreInspection block,
        List<DerivedRecapRestoreDefect> defects
    ) {
        if (block.Final is not FinalRecapBlockHealth.Healthy) {
            AddInvalidCapability(defects, plan);
            return null;
        }
        return new NoOpRestoreAction(
            plan,
            block.WriteAuthority
        );
    }

    private async ValueTask<RestoreBlockExecution>
        ExecuteBlockAsync(
        PublishedRestoreHandle handle,
        RestoreBlockAction action,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        > windows,
        CancellationToken cancellationToken
    ) {
        switch (action) {
            case NoOpRestoreAction noOp:
                return RestoreBlockExecution.Succeeded(
                    noOp.WriteAuthority
                );
            case InstallFinalAction install:
                return await InstallFinalAsync(
                        handle,
                        install,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            case MaintainRestoreAction maintain:
                return await ExecuteMaintainAsync(
                        handle,
                        maintain,
                        windows,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            default:
                throw new InvalidOperationException(
                    "Unknown Published Restore action."
                );
        }
    }

    private async ValueTask<RestoreBlockExecution>
        ExecuteMaintainAsync(
        PublishedRestoreHandle handle,
        MaintainRestoreAction action,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        > windows,
        CancellationToken cancellationToken
    ) {
        DerivedRecapBlock? currentBlock = action.InitialBlock;
        PublishedBlockWriteAuthority writeAuthority =
            action.WriteAuthority;
        for (int index = action.NextEndpointIndex;
             index < action.MaintainPlan.CatchUpBoundaries.Count;
             index++) {
            cancellationToken.ThrowIfCancellationRequested();
            RecapMaintainerStepResult step =
                await RecapMaintainerStepRunner.RunAsync(
                        action.Maintainer,
                        action.MaintainPlan,
                        currentBlock,
                        windows[(action.Plan.RecapBlockId, index)],
                        action.MaintainPlan
                            .CatchUpBoundaries[index].Address,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            switch (step) {
                case RecapMaintainerStepResult.MaintainerFailed failed:
                    return RestoreBlockExecution.Failed(
                        new DerivedRecapRestoreResult.BlockFailed(
                            action.Plan.RecapBlockId,
                            DerivedRecapRestoreDefectCodes
                                .MaintainerFailed,
                            failed.Detail
                        )
                    );
                case RecapMaintainerStepResult.ResultInvalid invalid:
                    return RestoreBlockExecution.Failed(
                        new DerivedRecapRestoreResult.BlockFailed(
                            action.Plan.RecapBlockId,
                            DerivedRecapRestoreDefectCodes
                                .MaintainerResultInvalid,
                            invalid.Detail
                        )
                    );
                case RecapMaintainerStepResult.Succeeded succeeded:
                    currentBlock = succeeded.Candidate;
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unknown Maintainer step result."
                    );
            }

            PublishedCheckpointWriteResult checkpoint =
                await _store.AdvancePublishedCheckpointAsync(
                        writeAuthority,
                        currentBlock,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            switch (checkpoint) {
                case PublishedCheckpointWriteResult.Updated updated:
                    writeAuthority = updated.WriteAuthority;
                    break;
                case PublishedCheckpointWriteResult.AlreadyCurrent current:
                    writeAuthority = current.WriteAuthority;
                    break;
                case PublishedCheckpointWriteResult.Stale:
                    return ConcurrentChange(
                        action.Plan.RecapBlockId,
                        "Published checkpoint changed concurrently."
                    );
                case PublishedCheckpointWriteResult.Unavailable unavailable:
                    return RestoreBlockExecution.Failed(
                        Unavailable(unavailable.Defects)
                    );
                default:
                    throw new InvalidOperationException(
                        "Unknown Published checkpoint write result."
                    );
            }
        }

        if (currentBlock is null) {
            return RestoreBlockExecution.Failed(
                Unavailable(
                    DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
                    $"Maintain block '{action.Plan.RecapBlockId}' "
                    + "produced no final checkpoint."
                )
            );
        }
        return await InstallFinalAsync(
                handle,
                new InstallFinalAction(
                    action.Plan,
                    writeAuthority,
                    currentBlock
                ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private async ValueTask<RestoreBlockExecution> InstallFinalAsync(
        PublishedRestoreHandle handle,
        InstallFinalAction action,
        CancellationToken cancellationToken
    ) {
        PublishedFinalWriteResult final =
            await _store.InstallPublishedReplacementAsync(
                    action.WriteAuthority,
                    action.Candidate,
                    cancellationToken
                )
                .ConfigureAwait(false);
        return final switch {
            PublishedFinalWriteResult.Installed installed =>
                RestoreBlockExecution.Succeeded(
                    installed.WriteAuthority
                ),
            PublishedFinalWriteResult.ReplacedDamaged replaced =>
                RestoreBlockExecution.Succeeded(
                    replaced.WriteAuthority
                ),
            PublishedFinalWriteResult.AlreadyHealthy healthy =>
                RestoreBlockExecution.Succeeded(
                    healthy.WriteAuthority
                ),
            PublishedFinalWriteResult.HealthyConflict =>
                ConcurrentChange(
                    action.Plan.RecapBlockId,
                    "Published final block was concurrently installed "
                    + "with different bytes."
                ),
            PublishedFinalWriteResult.Stale =>
                ConcurrentChange(
                    action.Plan.RecapBlockId,
                    "Published final block changed concurrently."
                ),
            PublishedFinalWriteResult.Unavailable unavailable =>
                RestoreBlockExecution.Failed(
                    Unavailable(unavailable.Defects)
                ),
            _ => throw new InvalidOperationException(
                "Unknown Published final write result."
            )
        };
    }

    private static RestoreBlockExecution ConcurrentChange(
        RecapBlockId blockId,
        string detail
    ) => RestoreBlockExecution.Failed(
        new DerivedRecapRestoreResult.Retryable(
            DerivedRecapRestoreDefectCodes.ConcurrentPublishedChange,
            $"Block '{blockId}': {detail}"
        )
    );

    private static DerivedRecapRestoreResult.Retryable RawHeadChanged(
        EventAddress expected,
        EventAddress? observed
    ) => new(
        DerivedRecapRestoreDefectCodes.RawHeadChanged,
        $"Raw SessionJournal head changed. Expected '{expected}', "
        + $"observed '{observed?.ToString() ?? "<none>"}'."
    );

    private static DerivedRecapRestoreResult.Unavailable Unavailable(
        IReadOnlyList<RecapStructuralDefect> defects
    ) => new([
        .. defects.Select(static defect =>
            new DerivedRecapRestoreDefect(
                defect.Code,
                defect.Detail
            )
        )
    ]);

    private static DerivedRecapRestoreResult.Unavailable Unavailable(
        string code,
        string detail
    ) => new([new DerivedRecapRestoreDefect(code, detail)]);

    private static void AddInvalidCapability(
        List<DerivedRecapRestoreDefect> defects,
        RecapBlockPlan plan
    ) => AddDefect(
        defects,
        DerivedRecapRestoreDefectCodes.FrozenPlanInvalid,
        $"Block '{plan.RecapBlockId}' has an inconsistent Restore "
        + "capability."
    );

    private static void AddDependencyUnavailable(
        List<DerivedRecapRestoreDefect> defects,
        RecapBlockPlan plan
    ) => AddDefect(
        defects,
        "RestoreDependencyUnavailable",
        $"Block '{plan.RecapBlockId}' lacks its exact frozen input."
    );

    private static void AddDefect(
        List<DerivedRecapRestoreDefect> defects,
        string code,
        string detail
    ) => defects.Add(new DerivedRecapRestoreDefect(code, detail));

    private static bool IsAvailabilityException(Exception exception)
        => exception is InvalidDataException
            or InvalidOperationException
            or ArgumentException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or KeyNotFoundException;

    private sealed record PreparedRestore(
        IReadOnlyList<DerivedRecapRestoreDefect> Defects,
        IReadOnlyList<RestoreBlockAction> Actions,
        IReadOnlyDictionary<
            (RecapBlockId BlockId, int EndpointIndex),
            SessionHistoryPlanningWindow
        > Windows,
        SessionCurrentLineageBeyondPrefix? BeyondPrefix = null
    ) {
        public static PreparedRestore Unavailable(
            IReadOnlyList<DerivedRecapRestoreDefect> defects
        ) => new(
            defects,
            [],
            new Dictionary<
                (RecapBlockId BlockId, int EndpointIndex),
                SessionHistoryPlanningWindow
            >()
        );
    }

    private abstract record RestoreBlockAction(
        RecapBlockPlan Plan,
        PublishedBlockWriteAuthority WriteAuthority
    );

    private sealed record NoOpRestoreAction(
        RecapBlockPlan Plan,
        PublishedBlockWriteAuthority WriteAuthority
    ) : RestoreBlockAction(Plan, WriteAuthority);

    private sealed record InstallFinalAction(
        RecapBlockPlan Plan,
        PublishedBlockWriteAuthority WriteAuthority,
        DerivedRecapBlock Candidate
    ) : RestoreBlockAction(Plan, WriteAuthority);

    private sealed record MaintainRestoreAction(
        MaintainRecapBlockPlan MaintainPlan,
        PublishedBlockWriteAuthority WriteAuthority,
        DerivedRecapBlock? InitialBlock,
        int NextEndpointIndex,
        EventAddress StartExclusive,
        IRecapBlockMaintainer Maintainer
    ) : RestoreBlockAction(MaintainPlan, WriteAuthority);

    private sealed record RestoreBlockExecution(
        PublishedBlockWriteAuthority? WriteAuthority,
        DerivedRecapRestoreResult? Failure
    ) {
        public static RestoreBlockExecution Succeeded(
            PublishedBlockWriteAuthority writeAuthority
        ) => new(writeAuthority, null);

        public static RestoreBlockExecution Failed(
            DerivedRecapRestoreResult failure
        ) => new(null, failure);
    }
}
