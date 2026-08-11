using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal sealed record RuntimeItemResult(
    PreparedRecapWork Prepared,
    RecapCellExecutionOutcome? Outcome,
    ExceptionDispatchInfo? Fatal
);

internal readonly record struct RuntimeLeaderAdmission(
    bool CanDispatch,
    TimeSpan Wait
);

internal sealed class RuntimeFatalLatch {
    private int _set;

    internal bool IsSet => Volatile.Read(ref _set) != 0;
    internal void Set() => Interlocked.Exchange(ref _set, 1);
}

public sealed partial class RecapCompletionRuntime {
    private readonly object _laneGate = new();
    private readonly Dictionary<RecapCompletionRoute, RuntimeLane> _lanes =
        new(ReferenceEqualityComparer.Instance);

    private async ValueTask<RecapCellBatchExecutionResult> RunPreparedAsync(
        IReadOnlyList<PreparedRecapWork> prepared,
        CancellationToken cancellationToken
    ) {
        if (cancellationToken.IsCancellationRequested) {
            return CancelAll(prepared);
        }
        IReadOnlyList<IReadOnlyList<PreparedRecapWork>> groups = prepared
            .GroupBy(static item => item.Route, ReferenceEqualityComparer.Instance)
            .Select(static group =>
                (IReadOnlyList<PreparedRecapWork>)group.ToArray())
            .ToArray();
        var barrier = new RuntimeLeaderBarrier(groups.Count);
        var fatalLatch = new RuntimeFatalLatch();
        Task<IReadOnlyList<RuntimeItemResult>>[] groupTasks = [.. groups
            .Select(group => RunGroupAsync(
                group,
                barrier,
                fatalLatch,
                cancellationToken
            ))];
        await barrier.AllReady.ConfigureAwait(false);
        barrier.Release();
        IReadOnlyList<RuntimeItemResult>[] results =
            await Task.WhenAll(groupTasks).ConfigureAwait(false);

        RuntimeItemResult[] ordered = results
            .SelectMany(static value => value)
            .OrderBy(static value => value.Prepared.Work.Ordinal)
            .ToArray();
        ExceptionDispatchInfo? fatal = ordered
            .Where(static value => value.Fatal is not null)
            .Select(static value => value.Fatal)
            .FirstOrDefault();
        fatal?.Throw();
        return new RecapCellBatchExecutionResult.Completed(
            ordered.Select(static value => value.Outcome!).ToArray()
        );
    }

    private async Task<IReadOnlyList<RuntimeItemResult>> RunGroupAsync(
        IReadOnlyList<PreparedRecapWork> group,
        RuntimeLeaderBarrier barrier,
        RuntimeFatalLatch fatalLatch,
        CancellationToken cancellationToken
    ) {
        RuntimeLeaderAdmission admission;
        try {
            admission = PreAdmitLeader(
                group[0],
                cancellationToken
            );
        }
        finally {
            barrier.PreAdmissionDecided();
        }
        await barrier.Dispatch.ConfigureAwait(false);
        if (!admission.CanDispatch) {
            barrier.LeaderStartedOrTerminal();
            await barrier.AllLeadersStartedOrTerminal.ConfigureAwait(false);
            return [.. group.Select(static item =>
                new RuntimeItemResult(
                    item,
                    new RecapCellExecutionOutcome
                        .NotStartedDueToCallerCancellation(
                            item.Work.EvaluationKey.Digest
                        ),
                    null
                ))];
        }
        RuntimeItemResult leader = await RunOneAsync(
            group[0],
            RuntimeLanePriority.Leader,
            fatalLatch,
            barrier.LeaderStartedOrTerminal,
            admission.Wait,
            cancellationToken
        ).ConfigureAwait(false);
        await barrier.AllLeadersStartedOrTerminal.ConfigureAwait(false);
        if (leader.Fatal is not null
            || fatalLatch.IsSet
            || cancellationToken.IsCancellationRequested) {
            return [leader, .. group.Skip(1).Select(static item =>
                new RuntimeItemResult(
                    item,
                    new RecapCellExecutionOutcome
                        .NotStartedDueToCallerCancellation(
                            item.Work.EvaluationKey.Digest
                        ),
                    null
                ))];
        }
        Task<RuntimeItemResult>[] followers = [.. group.Skip(1).Select(
            item => RunOneAsync(
                item,
                RuntimeLanePriority.Follower,
                fatalLatch,
                startedOrTerminal: null,
                admissionWait: TimeSpan.Zero,
                callerToken: cancellationToken
            ))];
        if (followers.Length == 0) { return [leader]; }
        RuntimeItemResult[] settled = await Task.WhenAll(followers)
            .ConfigureAwait(false);
        return [leader, .. settled];
    }

    private RuntimeLeaderAdmission PreAdmitLeader(
        PreparedRecapWork prepared,
        CancellationToken cancellationToken
    ) {
        Stopwatch wait = Stopwatch.StartNew();
        _ = GetLane(prepared.Route);
        return new RuntimeLeaderAdmission(
            !cancellationToken.IsCancellationRequested,
            wait.Elapsed
        );
    }

    private async Task<RuntimeItemResult> RunOneAsync(
        PreparedRecapWork prepared,
        RuntimeLanePriority priority,
        RuntimeFatalLatch fatalLatch,
        Action? startedOrTerminal,
        TimeSpan admissionWait,
        CancellationToken callerToken
    ) {
        RuntimeLane lane = GetLane(prepared.Route);
        RuntimeLane.Lease? laneLease = null;
        CancellationTokenSource? timeout = null;
        bool started = false;
        Stopwatch elapsed = Stopwatch.StartNew();
        Stopwatch laneWaitClock = Stopwatch.StartNew();
        TimeSpan laneWait = TimeSpan.Zero;
        CompletionResult? completionResult = null;
        string providerOutcome = "not-started";
        string? telemetryCode = null;
        string? telemetryDetail = null;
        bool decisionSignaled = false;
        void SignalStartedOrTerminal() {
            if (decisionSignaled) { return; }
            decisionSignaled = true;
            startedOrTerminal?.Invoke();
        }
        try {
            laneLease = await lane.AcquireAsync(priority, callerToken)
                .ConfigureAwait(false);
            laneWait = laneWaitClock.Elapsed;
            if (callerToken.IsCancellationRequested) {
                providerOutcome = "caller-cancelled-before-dispatch";
                return NotStarted(prepared);
            }
            if (fatalLatch.IsSet) {
                providerOutcome = "fatal-latched-before-dispatch";
                telemetryCode = "BatchFatalLatched";
                return NotStarted(prepared);
            }
            started = true;
            timeout = new CancellationTokenSource(
                prepared.Route.DispatchTimeout
            );
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                timeout.Token
            );
            SignalStartedOrTerminal();
            completionResult = await prepared.Route.Invoker.InvokeAsync(
                prepared.Request,
                _options.InvocationOptions,
                linked.Token
            ).ConfigureAwait(false);
            RuntimeParseResult parsed = RuntimeParser.Parse(
                prepared,
                completionResult
            );
            return parsed switch {
                RuntimeParseResult.Parsed value => new RuntimeItemResult(
                    prepared,
                    RecordParsedOutcome(value.Outcome, out providerOutcome),
                    null
                ),
                RuntimeParseResult.Failed value => Failure(
                    prepared,
                    telemetryCode = value.Code,
                    telemetryDetail = value.Detail,
                    out providerOutcome
                ),
                _ => Failure(
                    prepared,
                    telemetryCode = "ParserContractInvalid",
                    telemetryDetail =
                        "The output parser returned an unsupported result.",
                    out providerOutcome
                )
            };
        }
        catch (OperationCanceledException) when (!started
            && callerToken.IsCancellationRequested) {
            laneWait = laneWaitClock.Elapsed;
            providerOutcome = "caller-cancelled-before-dispatch";
            return NotStarted(prepared);
        }
        catch (OperationCanceledException exception) {
            string code = callerToken.IsCancellationRequested
                ? "CallerCancelledAfterDispatch"
                : timeout?.IsCancellationRequested == true
                    ? "CompletionTimeout"
                    : "CompletionProviderCancelled";
            return Failure(
                prepared,
                telemetryCode = code,
                telemetryDetail = exception.Message,
                out providerOutcome
            );
        }
        catch (Exception exception) when (IsFatal(exception)) {
            fatalLatch.Set();
            providerOutcome = "fatal";
            telemetryCode = "CompletionProviderFatal";
            return new RuntimeItemResult(
                prepared,
                null,
                ExceptionDispatchInfo.Capture(exception)
            );
        }
        catch (Exception exception) {
            return Failure(
                prepared,
                telemetryCode = "CompletionProviderFailure",
                telemetryDetail = exception.Message,
                out providerOutcome
            );
        }
        finally {
            SignalStartedOrTerminal();
            timeout?.Dispose();
            laneLease?.Dispose();
            RecordTelemetry(
                prepared,
                priority,
                admissionWait,
                laneWait,
                elapsed.Elapsed,
                completionResult,
                providerOutcome,
                telemetryCode,
                telemetryDetail
            );
        }
    }

    private RuntimeLane GetLane(RecapCompletionRoute route) {
        lock (_laneGate) {
            if (_lanes.TryGetValue(route, out RuntimeLane? lane)) {
                return lane;
            }
            lane = new RuntimeLane(route.MaximumConcurrency);
            _lanes.Add(route, lane);
            return lane;
        }
    }

    private void RecordTelemetry(
        PreparedRecapWork prepared,
        RuntimeLanePriority priority,
        TimeSpan admissionWait,
        TimeSpan laneWait,
        TimeSpan elapsed,
        CompletionResult? result,
        string providerOutcome,
        string? code,
        string? detail
    ) {
        if (_telemetry is null) { return; }
        try {
            _telemetry.Record(new RecapCompletionTelemetryEvent(
                "completion-settled",
                prepared.Route.Key,
                prepared.Route.ModelId,
                prepared.Route.Invoker.ProviderId,
                prepared.Route.Invoker.ApiSpecId,
                prepared.Work.EvaluationKey.Digest,
                prepared.Work.Family.Digest,
                prepared.Work.Definition.Digest,
                prepared.Work.EvaluationKey.HistorySegmentDigest.Value,
                prepared.Work.EvaluationKey.PriorInput
                    is PriorInputReference.FirstRow,
                prepared.Work.EvaluationKey.PriorInput
                    is PriorInputReference.Projection projection
                        ? projection.Digest
                        : null,
                priority is RuntimeLanePriority.Leader
                    ? RecapCompletionWorkRole.Leader
                    : RecapCompletionWorkRole.Follower,
                admissionWait,
                laneWait,
                elapsed,
                _options.InvocationOptions.PromptCacheReuseHint,
                result is not null,
                result?.Termination.Kind,
                result?.Errors?.Count ?? 0,
                result?.Usage,
                providerOutcome,
                code,
                detail
            ));
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            // Operational telemetry is never an outcome dependency.
        }
    }

    private static RuntimeItemResult Failure(
        PreparedRecapWork prepared,
        string code,
        string detail,
        out string providerOutcome
    ) {
        providerOutcome = "failed";
        return new RuntimeItemResult(
            prepared,
            new RecapCellExecutionOutcome.Failed(
                prepared.Work.EvaluationKey.Digest,
                code,
                RuntimeDiagnostics.BoundDetail(detail)
            ),
            null
        );
    }

    private static RecapCellExecutionOutcome RecordParsedOutcome(
        RecapCellExecutionOutcome outcome,
        out string providerOutcome
    ) {
        providerOutcome = outcome switch {
            RecapCellExecutionOutcome.Updated => "updated",
            RecapCellExecutionOutcome.KeepUnchanged => "keep-unchanged",
            _ => "parsed"
        };
        return outcome;
    }

    private static RuntimeItemResult NotStarted(
        PreparedRecapWork prepared
    ) => new(
        prepared,
        new RecapCellExecutionOutcome.NotStartedDueToCallerCancellation(
            prepared.Work.EvaluationKey.Digest
        ),
        null
    );

    private static RecapCellBatchExecutionResult CancelAll(
        IReadOnlyList<PreparedRecapWork> prepared
    ) => new RecapCellBatchExecutionResult.Completed(
        prepared.Select(static item =>
            (RecapCellExecutionOutcome)new RecapCellExecutionOutcome
                .NotStartedDueToCallerCancellation(
                    item.Work.EvaluationKey.Digest
                )).ToArray()
    );

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    private sealed class RuntimeLeaderBarrier {
        private readonly TaskCompletionSource _allPreAdmissions = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _dispatch = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource _allLeadersStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _remainingPreAdmissions;
        private int _remainingLeaderStarts;

        internal RuntimeLeaderBarrier(int count) {
            _remainingPreAdmissions = count;
            _remainingLeaderStarts = count;
        }
        internal Task AllReady => _allPreAdmissions.Task;
        internal Task Dispatch => _dispatch.Task;
        internal Task AllLeadersStartedOrTerminal =>
            _allLeadersStarted.Task;

        internal void PreAdmissionDecided() {
            if (Interlocked.Decrement(ref _remainingPreAdmissions) == 0) {
                _allPreAdmissions.TrySetResult();
            }
        }

        internal void LeaderStartedOrTerminal() {
            if (Interlocked.Decrement(ref _remainingLeaderStarts) == 0) {
                _allLeadersStarted.TrySetResult();
            }
        }

        internal void Release() => _dispatch.TrySetResult();
    }
}
