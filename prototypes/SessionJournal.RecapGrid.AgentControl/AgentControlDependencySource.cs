using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.AgentControl;

internal sealed class AgentControlDependencySource : IDisposable {
    private readonly SessionJournalReadView _selectedRef;
    private readonly RecapGridControlAdmission _admission;
    private readonly AgentControlDependencyTestHooks? _hooks;
    private readonly Lazy<AgentControlDependencyResult> _lazy;
    private bool _disposed;

    internal AgentControlDependencySource(
        SessionJournalReadView selectedRef,
        RecapGridControlAdmission admission,
        AgentControlDependencyTestHooks? hooks
    ) {
        _selectedRef = selectedRef;
        _admission = admission;
        _hooks = hooks;
        _lazy = new Lazy<AgentControlDependencyResult>(
            OpenCore,
            LazyThreadSafetyMode.ExecutionAndPublication
        );
    }

    internal AgentControlDependencyResult Get() {
        lock (_lazy) {
            if (_disposed) {
                return new AgentControlDependencyResult.Failure(
                    "disposed",
                    "Agent Control binding is disposed."
                );
            }
            return _lazy.Value;
        }
    }

    public void Dispose() {
        AgentControlDependencyResult? opened = null;
        lock (_lazy) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            if (_lazy.IsValueCreated) {
                opened = _lazy.Value;
            }
        }
        if (opened is AgentControlDependencyResult.Opened value) {
            try {
                value.Control.Dispose();
            }
            finally {
                value.Timeline.Dispose();
            }
        }
    }

    private AgentControlDependencyResult OpenCore() {
        _hooks?.BeforeOpen?.Invoke();
        HistoryTimelineReaderHandle? timeline = null;
        RecapGridControlHandle? control = null;
        try {
            HistoryTimelineReaderOpenResult timelineOpen =
                HistoryTimelineMaintenance.OpenReader(
                    _selectedRef.Path,
                    _selectedRef.BranchRefId
                );
            switch (timelineOpen) {
                case HistoryTimelineReaderOpenResult.Opened value:
                    timeline = value.Handle;
                    break;
                case HistoryTimelineReaderOpenResult.Absent:
                    return Failure("timeline-absent", "Timeline is absent.");
                case HistoryTimelineReaderOpenResult.Busy:
                    return Failure("busy", "Timeline is busy.");
                case HistoryTimelineReaderOpenResult.UnsupportedSchema value:
                    return Failure(
                        "unsupported-schema",
                        $"timeline:{value.SchemaVersion}"
                    );
                case HistoryTimelineReaderOpenResult.Invalid value:
                    return Failure(value.Code, value.Detail);
                default:
                    return Failure(
                        "timeline-open-invalid",
                        "Timeline returned an unknown open outcome."
                    );
            }
            RecapGridControlOpenResult controlOpen =
                RecapGridControlFactory.Open(
                    _selectedRef.Path,
                    _selectedRef.BranchRefId,
                    _admission
                );
            switch (controlOpen) {
                case RecapGridControlOpenResult.Opened value:
                    control = value.Handle;
                    break;
                case RecapGridControlOpenResult.Absent:
                    return Failure("control-absent", "Control is absent.");
                case RecapGridControlOpenResult.TimelineAbsent:
                    return Failure("timeline-absent", "Timeline is absent.");
                case RecapGridControlOpenResult.Busy:
                    return Failure("busy", "Control is busy.");
                case RecapGridControlOpenResult.TimelineUnsupportedSchema value:
                    return Failure(
                        "unsupported-schema",
                        $"timeline:{value.SchemaVersion}"
                    );
                case RecapGridControlOpenResult.UnsupportedSchema value:
                    return Failure(
                        "unsupported-schema",
                        $"control:{value.SchemaVersion}"
                    );
                case RecapGridControlOpenResult.Invalid value:
                    return Failure(value.Code, value.Detail);
                default:
                    return Failure(
                        "control-open-invalid",
                        "Control returned an unknown open outcome."
                    );
            }
            AgentControlDependencyResult result =
                new AgentControlDependencyResult.Opened(
                    timeline,
                    control
                );
            timeline = null;
            control = null;
            return result;
        }
        catch (Exception exception) when (!IsFatal(exception)) {
            return Failure(
                "dependency-open-invalid",
                RecapGridAgentControlFactory.Bound(exception.Message)
            );
        }
        finally {
            control?.Dispose();
            timeline?.Dispose();
        }
    }

    private static AgentControlDependencyResult.Failure Failure(
        string code,
        string detail
    ) => new(code, detail);

    private static bool IsFatal(Exception exception)
        => exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;
}

internal abstract record AgentControlDependencyResult {
    private AgentControlDependencyResult() { }

    internal sealed record Opened(
        HistoryTimelineReaderHandle Timeline,
        RecapGridControlHandle Control
    ) : AgentControlDependencyResult;

    internal sealed record Failure(string Code, string Detail)
        : AgentControlDependencyResult;
}

internal sealed record AgentControlDependencyTestHooks(
    Action? BeforeOpen = null,
    Func<RecapGridControlOperationResult>?
        ControlOperationResultOverride = null,
    Func<CancellationToken, RecapGridBuildProgressResult>?
        ProgressResultOverride = null
);
