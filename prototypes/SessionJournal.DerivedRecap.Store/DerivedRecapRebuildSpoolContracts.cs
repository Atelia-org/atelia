using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

public sealed record DerivedRecapRebuildSpoolLimits(
    int PageEventCount,
    long MaximumPageBytes,
    long MaximumEventCount,
    long MaximumTotalEncodedBytes
) {
    public static DerivedRecapRebuildSpoolLimits Default { get; } =
        new(
            PageEventCount: 512,
            MaximumPageBytes: 512 * 1024,
            MaximumEventCount: 10_000_000,
            MaximumTotalEncodedBytes: 8L * 1024 * 1024 * 1024
        );
}

public sealed record DerivedRecapRebuildSpoolDescriptor(
    string CampaignId,
    SessionSelectedLineageAuditCapture Capture,
    DerivedRecapRebuildSpoolLimits Limits
);

public sealed record DerivedRecapRebuildSpoolCheckpoint(
    DerivedRecapRebuildSpoolDescriptor Descriptor,
    long CommittedPageCount,
    EventAddress? NextAddress,
    long EventCount,
    long LogicalPayloadBytes,
    long EncodedPageBytes,
    string PageChainSha256
) {
    public bool IsCaptureComplete => NextAddress is null;
}

public sealed record DerivedRecapRebuildSpoolSeal(
    DerivedRecapRebuildSpoolCheckpoint Checkpoint,
    EventAddress RootAddress,
    EventAddress BootstrapAddress,
    SessionContextAnchorSetupReferences BootstrapSetups,
    SessionContextAnchorSetupReferences HeadSetups,
    SessionExecutionPhase ExecutionPhase,
    SessionEventKind? HeadKind
);

public sealed class DerivedRecapRebuildSpoolWriter : IAsyncDisposable {
    private readonly DerivedRecapRebuildSpoolStore _owner;
    private readonly FileStream _lock;
    private bool _disposed;

    internal DerivedRecapRebuildSpoolWriter(
        DerivedRecapRebuildSpoolStore owner,
        FileStream writeLock,
        DerivedRecapRebuildSpoolCheckpoint checkpoint
    ) {
        _owner = owner;
        _lock = writeLock;
        Checkpoint = checkpoint;
    }

    public DerivedRecapRebuildSpoolCheckpoint Checkpoint {
        get;
        private set;
    }

    public IEnumerable<SessionSelectedLineageAuditPage>
        ReadCommittedPages() {
        ThrowIfDisposed();
        return _owner.ReadCommittedPages(
            Checkpoint.Descriptor.CampaignId,
            Checkpoint.CommittedPageCount
        );
    }

    public async ValueTask AppendPageAsync(
        SessionSelectedLineageAuditPage page,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        Checkpoint = await _owner.AppendPageUnderLockAsync(
                Checkpoint,
                page,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public ValueTask SealAsync(
        SessionSelectedLineageAuditAuthority authority,
        CancellationToken cancellationToken = default
    ) {
        ThrowIfDisposed();
        return _owner.SealUnderLockAsync(
            Checkpoint,
            authority,
            cancellationToken
        );
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) {
            return;
        }
        await _lock.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
