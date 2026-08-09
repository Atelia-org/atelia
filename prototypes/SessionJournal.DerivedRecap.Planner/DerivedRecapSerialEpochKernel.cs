using System.Text;
using Atelia.SessionJournal.DerivedRecap.Abstractions;
using Atelia.SessionJournal.DerivedRecap.Store;

namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal sealed record SerialEpochFailure(
    RecapBlockId RecapBlockId,
    string Code,
    string Detail
);

internal abstract record SerialEpochBlockOutcome(
    RecapBlockId RecapBlockId
) {
    public sealed record ReusedHealthy(RecapBlockId RecapBlockId)
        : SerialEpochBlockOutcome(RecapBlockId);

    public sealed record FinalInstalled(
        RecapBlockId RecapBlockId,
        bool KeptUnchanged
    ) : SerialEpochBlockOutcome(RecapBlockId);

    public sealed record Failed(
        RecapBlockId RecapBlockId,
        SerialEpochFailure Failure
    ) : SerialEpochBlockOutcome(RecapBlockId);
}

internal sealed record SerialEpochKernelResult(
    RecapMaintenanceEpochInput RuntimeInput,
    IReadOnlyList<SerialEpochBlockOutcome> Outcomes,
    SerialEpochFailure? PrimaryFailure,
    int StartedCallCount
) {
    public bool Succeeded => PrimaryFailure is null;
}

/// <summary>
/// Stage-neutral parallel scheduling kernel for one complete shared epoch.
/// Building and Published Restore retain their own Store authority and pass
/// only an authority-capturing final-install delegate into this kernel.
/// </summary>
internal static class DerivedRecapSerialEpochKernel {
    internal static async ValueTask<SerialEpochKernelResult> ExecuteAsync(
        RecapEpochStoreSnapshot snapshot,
        IRecapBlockMaintainerRegistry registry,
        int maxMaintainerCallsPerEpoch,
        int maxMaintainerCallsForOperation,
        Func<
            RecapEpochBlockInspection,
            DerivedRecapFinalBlock,
            CancellationToken,
            ValueTask<WriteRecapEpochFinalResult>
        > installFinal,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(installFinal);
        if (maxMaintainerCallsPerEpoch <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxMaintainerCallsPerEpoch)
            );
        }
        if (maxMaintainerCallsForOperation < 0) {
            throw new ArgumentOutOfRangeException(
                nameof(maxMaintainerCallsForOperation)
            );
        }
        DerivedRecapV8Codec.ValidateEpochSet(
            snapshot.Manifest,
            snapshot.EpochInput
        );
        if (snapshot.Blocks.Count != snapshot.Manifest.Blocks.Count) {
            throw new InvalidDataException(
                "Epoch inspection does not cover the complete manifest roster."
            );
        }

        RecapMaintenanceEpochInput runtimeInput = ProjectRuntimeInput(
            snapshot.EpochInput
        );
        var outcomes = new SerialEpochBlockOutcome?[snapshot.Blocks.Count];
        var pending = new List<PendingMaintainer>();
        for (int ordinal = 0; ordinal < snapshot.Blocks.Count; ordinal++) {
            RecapEpochBlockInspection inspection = snapshot.Blocks[ordinal];
            RecapEpochBlockDefinition definition =
                snapshot.Manifest.Blocks[ordinal];
            if (inspection.Definition != definition) {
                throw new InvalidDataException(
                    "Epoch inspection order differs from the manifest roster."
                );
            }
            if (inspection.Final is RecapEpochFinalHealth.Healthy) {
                outcomes[ordinal] =
                    new SerialEpochBlockOutcome.ReusedHealthy(
                        definition.RecapBlockId
                    );
                continue;
            }
            if (inspection.WriteAuthority is null) {
                pending.Add(PendingMaintainer.Unavailable(
                    ordinal,
                    inspection,
                    new SerialEpochFailure(
                        definition.RecapBlockId,
                        "FinalSlotUnavailable",
                        "Final slot cannot be read safely and has no write authority."
                    )
                ));
                continue;
            }
            if (!registry.TryResolve(
                    definition.MaintainerId,
                    definition.Target,
                    definition.MaintainerCapabilityFingerprint,
                    out IRecapBlockMaintainer? maintainer
                )) {
                pending.Add(PendingMaintainer.Unavailable(
                    ordinal,
                    inspection,
                    new SerialEpochFailure(
                        definition.RecapBlockId,
                        "MaintainerUnavailable",
                        "Frozen Maintainer binding is unavailable."
                    )
                ));
                continue;
            }
            if (!string.Equals(
                    maintainer.Id,
                    definition.MaintainerId,
                    StringComparison.Ordinal
                )
                || maintainer.Target != definition.Target
                || !string.Equals(
                    maintainer.CapabilityFingerprint,
                    definition.MaintainerCapabilityFingerprint,
                    StringComparison.Ordinal
                )) {
                pending.Add(PendingMaintainer.Unavailable(
                    ordinal,
                    inspection,
                    new SerialEpochFailure(
                        definition.RecapBlockId,
                        "MaintainerIdentityMismatch",
                        "Resolved Maintainer identity differs from frozen roster."
                    )
                ));
                continue;
            }
            if (maintainer.RuntimeGroupAffinity is null) {
                pending.Add(PendingMaintainer.Unavailable(
                    ordinal,
                    inspection,
                    new SerialEpochFailure(
                        definition.RecapBlockId,
                        "MaintainerRuntimeGroupUnavailable",
                        "Resolved Maintainer has no runtime group affinity."
                    )
                ));
                continue;
            }
            pending.Add(new PendingMaintainer(
                ordinal,
                inspection,
                maintainer,
                null
            ));
        }

        int requiredCalls = pending.Count;
        if (requiredCalls > maxMaintainerCallsPerEpoch) {
            throw new InvalidDataException(
                $"Pending epoch roster requires {requiredCalls} calls; "
                + $"limit is {maxMaintainerCallsPerEpoch}."
            );
        }
        if (requiredCalls > maxMaintainerCallsForOperation) {
            throw new InvalidDataException(
                $"Pending epoch roster requires {requiredCalls} calls; "
                + $"remaining operation limit is {maxMaintainerCallsForOperation}."
            );
        }

        List<PendingGroup> groups = CreateGroups(pending);
        foreach (PendingGroup group in groups) {
            try {
                IRecapMaintenanceGroupExecution execution = group.Members[0]
                    .Maintainer!
                    .CreateGroupExecution(runtimeInput)
                    ?? throw new InvalidOperationException(
                        "Maintainer returned a null group execution."
                    );
                if (!ReferenceEquals(
                        execution.RuntimeGroupAffinity,
                        group.RuntimeGroupAffinity
                    )
                    || !ReferenceEquals(execution.Input, runtimeInput)) {
                    throw new InvalidOperationException(
                        "Group execution does not preserve exact runtime-group and epoch-input identity."
                    );
                }
                group.Execution = execution;
            }
            catch (Exception exception)
                when (RecapNonFatalException.IsCatchable(exception)) {
                foreach (PendingMaintainer member in group.Members) {
                    member.PreflightFailure = new SerialEpochFailure(
                        member.Inspection.Definition.RecapBlockId,
                        "GroupExecutionUnavailable",
                        exception.Message
                    );
                }
            }
        }

        // Resolve and validate the complete pending roster before the first
        // remote call, including shared-prefix projection. A preflight defect
        // means zero calls for this attempt.
        SerialEpochFailure? preflightFailure = pending
            .Where(static item => item.PreflightFailure is not null)
            .OrderBy(static item => item.Ordinal)
            .Select(static item => item.PreflightFailure)
            .FirstOrDefault();
        if (preflightFailure is not null) {
            foreach (PendingMaintainer item in pending) {
                SerialEpochFailure failure = item.PreflightFailure
                    ?? new SerialEpochFailure(
                        item.Inspection.Definition.RecapBlockId,
                        "PreflightAborted",
                        "No Maintainer was called because complete-roster preflight failed."
                    );
                outcomes[item.Ordinal] =
                    new SerialEpochBlockOutcome.Failed(
                        failure.RecapBlockId,
                        failure
                    );
            }
            return Finish(
                runtimeInput,
                outcomes,
                preflightFailure,
                startedCallCount: 0
            );
        }

        cancellationToken.ThrowIfCancellationRequested();
        var attemptCounter = new AttemptCounter();
        var leaderBatch = new LeaderBatch(groups.Count);
        var scheduled = groups
            .Select(group => new ScheduledGroup(
                group,
                new MaintainerCallControl(
                    RecapMaintainerCallRole.Leader,
                    leaderBatch,
                    attemptCounter
                )
            ))
            .ToArray();
        Task[] groupTasks = [
            .. scheduled.Select(group => ExecuteGroupAsync(
                group,
                snapshot,
                outcomes,
                installFinal,
                leaderBatch,
                attemptCounter,
                cancellationToken
            ))
        ];

        // Every leader must either reach its dispatch boundary or settle
        // locally before any leader is released. This makes the lane's
        // leader-priority queue authoritative even for synchronously
        // completing test/provider clients.
        await leaderBatch.AllLeadersReady.ConfigureAwait(false);
        leaderBatch.Release();
        await Task.WhenAll(groupTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        SerialEpochFailure? primary = outcomes
            .OfType<SerialEpochBlockOutcome.Failed>()
            .Select(static outcome => outcome.Failure)
            .FirstOrDefault();
        return Finish(
            runtimeInput,
            outcomes,
            primary,
            attemptCounter.Value
        );
    }

    private static List<PendingGroup> CreateGroups(
        IReadOnlyList<PendingMaintainer> pending
    ) {
        var byAffinity = new Dictionary<object, PendingGroup>(
            ReferenceEqualityComparer.Instance
        );
        var groups = new List<PendingGroup>();
        foreach (PendingMaintainer item in pending) {
            if (item.Maintainer is null) {
                continue;
            }
            object affinity = item.Maintainer.RuntimeGroupAffinity;
            if (!byAffinity.TryGetValue(
                    affinity,
                    out PendingGroup? group
                )) {
                group = new PendingGroup(affinity);
                byAffinity.Add(affinity, group);
                groups.Add(group);
            }
            group.Members.Add(item);
        }
        return groups;
    }

    private static async Task ExecuteGroupAsync(
        ScheduledGroup scheduled,
        RecapEpochStoreSnapshot snapshot,
        SerialEpochBlockOutcome?[] outcomes,
        Func<
            RecapEpochBlockInspection,
            DerivedRecapFinalBlock,
            CancellationToken,
            ValueTask<WriteRecapEpochFinalResult>
        > installFinal,
        LeaderBatch leaderBatch,
        AttemptCounter attemptCounter,
        CancellationToken cancellationToken
    ) {
        PendingGroup group = scheduled.Group;
        var leaderSettled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Task leader = ExecuteMemberAsync(
            group.Members[0],
            group.Execution!,
            scheduled.LeaderControl,
            snapshot,
            outcomes,
            installFinal,
            leaderSettled,
            cancellationToken
        );
        bool releaseFollowers = await leaderSettled.Task
            .ConfigureAwait(false);

        // A leader that failed before entering dispatch still cannot release
        // followers ahead of leaders in other groups.
        await leaderBatch.Released.ConfigureAwait(false);
        await leaderBatch.AllLeaderAdmissionsRequested
            .ConfigureAwait(false);
        if (!releaseFollowers
            || cancellationToken.IsCancellationRequested
            || group.Members.Count == 1) {
            await leader.ConfigureAwait(false);
            return;
        }
        Task[] followers = [
            .. group.Members.Skip(1).Select(member =>
                ExecuteMemberAsync(
                    member,
                    group.Execution!,
                    new MaintainerCallControl(
                        RecapMaintainerCallRole.Follower,
                        leaderBatch,
                        attemptCounter
                    ),
                    snapshot,
                    outcomes,
                    installFinal,
                    maintenanceSettled: null,
                    cancellationToken
                ))
        ];
        await Task.WhenAll([leader, .. followers]).ConfigureAwait(false);
    }

    private static async Task ExecuteMemberAsync(
        PendingMaintainer item,
        IRecapMaintenanceGroupExecution groupExecution,
        MaintainerCallControl callControl,
        RecapEpochStoreSnapshot snapshot,
        SerialEpochBlockOutcome?[] outcomes,
        Func<
            RecapEpochBlockInspection,
            DerivedRecapFinalBlock,
            CancellationToken,
            ValueTask<WriteRecapEpochFinalResult>
        > installFinal,
        TaskCompletionSource<bool>? maintenanceSettled,
        CancellationToken cancellationToken
    ) {
        RecapEpochBlockDefinition definition = item.Inspection.Definition;
        RecapMaintenanceSuccess result;
        bool releaseFollowers = false;
        try {
            result = await item.Maintainer!.MaintainAsync(
                    groupExecution,
                    callControl,
                    cancellationToken
                )
                .ConfigureAwait(false);
            releaseFollowers = true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested) {
            return;
        }
        catch (Exception exception)
            when (RecapNonFatalException.IsCatchable(exception)) {
            releaseFollowers = true;
            SetFailure(
                outcomes,
                item,
                "MaintainerFailed",
                exception.Message
            );
            return;
        }
        finally {
            callControl.SignalSettledBeforeDispatch();
            maintenanceSettled?.TrySetResult(releaseFollowers);
        }

        if (!callControl.HasStarted) {
            SetFailure(
                outcomes,
                item,
                "MaintainerDispatchContractViolated",
                "Maintainer returned success without starting its authorized remote dispatch."
            );
            return;
        }

        bool keptUnchanged = result
            is RecapMaintenanceSuccess.KeepUnchanged;
        string? content = result switch {
            RecapMaintenanceSuccess.Updated updated => updated.Content,
            RecapMaintenanceSuccess.KeepUnchanged =>
                FindPriorContent(snapshot.EpochInput, definition),
            _ => null
        };
        string? invalid = ValidateContent(definition, content);
        if (invalid is not null) {
            SetFailure(
                outcomes,
                item,
                "MaintainerResultInvalid",
                invalid
            );
            return;
        }

        DerivedRecapFinalBlock candidate;
        try {
            candidate = DerivedRecapV8Codec.CreateFinalBlock(
                snapshot.Manifest,
                definition,
                content!
            );
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                  or ArgumentException
                  or EncoderFallbackException) {
            SetFailure(
                outcomes,
                item,
                "MaintainerResultInvalid",
                exception.Message
            );
            return;
        }

        WriteRecapEpochFinalResult write;
        try {
            write = await installFinal(
                    item.Inspection,
                    candidate,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested) {
            return;
        }
        catch (Exception exception)
            when (RecapNonFatalException.IsCatchable(exception)) {
            SetFailure(
                outcomes,
                item,
                "FinalWriteFailed",
                exception.Message
            );
            return;
        }
        switch (write) {
            case WriteRecapEpochFinalResult.Installed:
            case WriteRecapEpochFinalResult.AlreadyHealthy:
                outcomes[item.Ordinal] =
                    new SerialEpochBlockOutcome.FinalInstalled(
                        definition.RecapBlockId,
                        keptUnchanged
                    );
                break;
            default:
                SetFailure(
                    outcomes,
                    item,
                    "FinalWriteRejected",
                    DescribeWriteFailure(write)
                );
                break;
        }
    }

    private static void SetFailure(
        SerialEpochBlockOutcome?[] outcomes,
        PendingMaintainer item,
        string code,
        string detail
    ) {
        var failure = new SerialEpochFailure(
            item.Inspection.Definition.RecapBlockId,
            code,
            detail
        );
        outcomes[item.Ordinal] = new SerialEpochBlockOutcome.Failed(
            failure.RecapBlockId,
            failure
        );
    }

    internal static RecapMaintenanceEpochInput ProjectRuntimeInput(
        DerivedRecapEpochInput input
    ) {
        DerivedRecapV8Codec.ValidateEpochInput(input);
        var pack = new ContextHeaderPack();
        if (input.Previous is RecapEpochPrevious.Prior prior) {
            foreach (PriorRecapBlockSnapshot block in prior.Pack.Blocks) {
                GetCarrier(pack, block.Target.Carrier).Add(
                    block.Target.BlockKey,
                    new ContextHeaderBlock(block.Content)
                );
            }
        }
        return new RecapMaintenanceEpochInput(
            pack.Render(),
            input.HistoryMessages,
            sourceId: input.PayloadSha256
        );
    }

    private static OrderedDictionary<string, ContextHeaderBlock> GetCarrier(
        ContextHeaderPack pack,
        ContextHeaderCarrier carrier
    ) => carrier switch {
        ContextHeaderCarrier.System => pack.System,
        ContextHeaderCarrier.Observation => pack.Observation,
        ContextHeaderCarrier.Action => pack.Action,
        _ => throw new InvalidDataException(
            "Prior recap block has an unsupported carrier."
        )
    };

    private static string? FindPriorContent(
        DerivedRecapEpochInput input,
        RecapEpochBlockDefinition definition
    ) {
        if (input.Previous is not RecapEpochPrevious.Prior prior) {
            return null;
        }
        PriorRecapBlockSnapshot? block = prior.Pack.Blocks
            .SingleOrDefault(candidate =>
                candidate.RecapBlockId == definition.RecapBlockId);
        return block is not null && block.Target == definition.Target
            ? block.Content
            : null;
    }

    private static string? ValidateContent(
        RecapEpochBlockDefinition definition,
        string? content
    ) {
        if (string.IsNullOrEmpty(content)) {
            return "Maintainer must return non-empty block content; bootstrap KeepUnchanged is invalid.";
        }
        try {
            int bytes = new UTF8Encoding(false, true).GetByteCount(content);
            if (bytes > definition.MaxContentUtf8Bytes) {
                return $"Maintainer result is {bytes} UTF-8 bytes; block limit is "
                    + $"{definition.MaxContentUtf8Bytes}.";
            }
        }
        catch (EncoderFallbackException) {
            return "Maintainer result content is not valid UTF-8.";
        }
        return null;
    }

    private static string DescribeWriteFailure(
        WriteRecapEpochFinalResult result
    ) => result switch {
        WriteRecapEpochFinalResult.HealthyConflict =>
            "A different healthy final already exists.",
        WriteRecapEpochFinalResult.Stale stale =>
            $"Final write authority is stale: {stale.CurrentStateToken}",
        WriteRecapEpochFinalResult.Invalid invalid => invalid.Detail,
        _ => $"Unexpected final write result '{result.GetType().Name}'."
    };

    private static SerialEpochKernelResult Finish(
        RecapMaintenanceEpochInput runtimeInput,
        SerialEpochBlockOutcome?[] outcomes,
        SerialEpochFailure? primary,
        int startedCallCount
    ) {
        if (outcomes.Any(static outcome => outcome is null)) {
            throw new InvalidDataException(
                "Serial epoch kernel did not produce one outcome per roster member."
            );
        }
        return new SerialEpochKernelResult(
            runtimeInput,
            Array.AsReadOnly(outcomes.Cast<SerialEpochBlockOutcome>().ToArray()),
            primary,
            startedCallCount
        );
    }

    private sealed class PendingMaintainer {
        internal PendingMaintainer(
            int ordinal,
            RecapEpochBlockInspection inspection,
            IRecapBlockMaintainer? maintainer,
            SerialEpochFailure? preflightFailure
        ) {
            Ordinal = ordinal;
            Inspection = inspection;
            Maintainer = maintainer;
            PreflightFailure = preflightFailure;
        }

        internal int Ordinal { get; }

        internal RecapEpochBlockInspection Inspection { get; }

        internal IRecapBlockMaintainer? Maintainer { get; }

        internal SerialEpochFailure? PreflightFailure { get; set; }

        internal static PendingMaintainer Unavailable(
            int ordinal,
            RecapEpochBlockInspection inspection,
            SerialEpochFailure failure
        ) => new(ordinal, inspection, null, failure);
    }

    private sealed class PendingGroup(object runtimeGroupAffinity) {
        internal object RuntimeGroupAffinity { get; } =
            runtimeGroupAffinity;

        internal List<PendingMaintainer> Members { get; } = [];

        internal IRecapMaintenanceGroupExecution? Execution { get; set; }
    }

    private sealed record ScheduledGroup(
        PendingGroup Group,
        MaintainerCallControl LeaderControl
    );

    private sealed class AttemptCounter {
        private int _value;

        internal int Value => Volatile.Read(ref _value);

        internal void Increment() => Interlocked.Increment(ref _value);
    }

    private sealed class LeaderBatch {
        private readonly TaskCompletionSource _allLeadersReady = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _allAdmissionsRequested = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _released = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _remaining;
        private int _remainingAdmissions;
        private int _releaseState;

        internal LeaderBatch(int leaderCount) {
            if (leaderCount < 0) {
                throw new ArgumentOutOfRangeException(nameof(leaderCount));
            }
            _remaining = leaderCount;
            _remainingAdmissions = leaderCount;
            if (leaderCount == 0) {
                _allLeadersReady.SetResult();
                _allAdmissionsRequested.SetResult();
            }
        }

        internal Task AllLeadersReady => _allLeadersReady.Task;

        internal Task Released => _released.Task;

        internal Task AllLeaderAdmissionsRequested =>
            _allAdmissionsRequested.Task;

        internal void SignalLeaderReady() {
            int remaining = Interlocked.Decrement(ref _remaining);
            if (remaining == 0) {
                _allLeadersReady.TrySetResult();
            }
            else if (remaining < 0) {
                throw new InvalidOperationException(
                    "A leader signalled readiness more than once."
                );
            }
        }

        internal void SignalLeaderAdmissionRequested() {
            int remaining = Interlocked.Decrement(
                ref _remainingAdmissions
            );
            if (remaining == 0) {
                _allAdmissionsRequested.TrySetResult();
            }
            else if (remaining < 0) {
                throw new InvalidOperationException(
                    "A leader signalled lane admission more than once."
                );
            }
        }

        internal void Release() {
            if (Interlocked.Exchange(ref _releaseState, 1) != 0) {
                throw new InvalidOperationException(
                    "Leader batch was released more than once."
                );
            }
            _released.TrySetResult();
        }
    }

    private sealed class MaintainerCallControl
        : IRecapMaintainerCallControl {
        private readonly LeaderBatch _leaderBatch;
        private readonly AttemptCounter _attemptCounter;
        private int _readyState;
        private int _permissionState;
        private int _admissionState;
        private int _startedState;

        internal MaintainerCallControl(
            RecapMaintainerCallRole role,
            LeaderBatch leaderBatch,
            AttemptCounter attemptCounter
        ) {
            Role = role;
            _leaderBatch = leaderBatch;
            _attemptCounter = attemptCounter;
        }

        public RecapMaintainerCallRole Role { get; }

        internal bool HasStarted =>
            Volatile.Read(ref _startedState) != 0;

        public async ValueTask WaitForDispatchPermissionAsync(
            CancellationToken cancellationToken
        ) {
            if (Interlocked.Exchange(ref _permissionState, 1) != 0) {
                throw new InvalidOperationException(
                    "Dispatch permission can be requested only once."
                );
            }
            if (Role == RecapMaintainerCallRole.Leader) {
                SignalLeaderReadyOnce();
                await _leaderBatch.Released.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else {
                cancellationToken.ThrowIfCancellationRequested();
            }
            Volatile.Write(ref _permissionState, 2);
        }

        public void MarkDispatchStarted() {
            if (Volatile.Read(ref _admissionState) != 1) {
                throw new InvalidOperationException(
                    "Dispatch cannot start before lane admission."
                );
            }
            if (Volatile.Read(ref _permissionState) != 2) {
                throw new InvalidOperationException(
                    "Dispatch cannot start before scheduler permission."
                );
            }
            if (Interlocked.Exchange(ref _startedState, 1) != 0) {
                throw new InvalidOperationException(
                    "A logical Maintainer invocation may dispatch only once."
                );
            }
            _attemptCounter.Increment();
        }

        public void MarkLaneAdmissionRequested() {
            if (Volatile.Read(ref _permissionState) != 2) {
                throw new InvalidOperationException(
                    "Lane admission cannot start before scheduler permission."
                );
            }
            if (Interlocked.Exchange(ref _admissionState, 1) != 0) {
                throw new InvalidOperationException(
                    "Lane admission can be requested only once."
                );
            }
            if (Role == RecapMaintainerCallRole.Leader) {
                _leaderBatch.SignalLeaderAdmissionRequested();
            }
        }

        internal void SignalSettledBeforeDispatch() {
            if (Role == RecapMaintainerCallRole.Leader) {
                SignalLeaderReadyOnce();
                SignalLeaderAdmissionOnce();
            }
        }

        private void SignalLeaderReadyOnce() {
            if (Interlocked.Exchange(ref _readyState, 1) == 0) {
                _leaderBatch.SignalLeaderReady();
            }
        }

        private void SignalLeaderAdmissionOnce() {
            if (Interlocked.Exchange(ref _admissionState, 1) == 0) {
                _leaderBatch.SignalLeaderAdmissionRequested();
            }
        }
    }
}
