namespace Atelia.StateJournal;

internal enum RepositoryCommitFaultPoint {
    BeforeDataDurabilityFlush = 0,
    BeforePrimaryRefPublication,
    DuringPrimaryRefPublication,
    BeforeReflogAppend,
    BeforeFormerActiveSegmentDispose,
}

public sealed partial class Repository {
    private static readonly AsyncLocal<CommitFaultInjection?> s_commitFaultInjection = new();

    internal static IDisposable InjectCommitFaultScope(
        RepositoryCommitFaultPoint faultPoint,
        Func<Exception> exceptionFactory
    ) {
        ArgumentNullException.ThrowIfNull(exceptionFactory);

        var previous = s_commitFaultInjection.Value;
        s_commitFaultInjection.Value = new CommitFaultInjection(faultPoint, exceptionFactory);
        return new CommitFaultScope(previous);
    }

    private static void ThrowIfCommitFaultInjected(RepositoryCommitFaultPoint faultPoint) {
        var injection = s_commitFaultInjection.Value;
        if (injection is null || !injection.Armed || injection.FaultPoint != faultPoint) { return; }
        injection.Armed = false;
        throw injection.ExceptionFactory();
    }

    private sealed class CommitFaultInjection(
        RepositoryCommitFaultPoint faultPoint,
        Func<Exception> exceptionFactory
    ) {
        public RepositoryCommitFaultPoint FaultPoint { get; } = faultPoint;
        public Func<Exception> ExceptionFactory { get; } = exceptionFactory;
        public bool Armed { get; set; } = true;
    }

    private sealed class CommitFaultScope(CommitFaultInjection? previous) : IDisposable {
        private bool _disposed;

        public void Dispose() {
            if (_disposed) { return; }
            s_commitFaultInjection.Value = previous;
            _disposed = true;
        }
    }
}
