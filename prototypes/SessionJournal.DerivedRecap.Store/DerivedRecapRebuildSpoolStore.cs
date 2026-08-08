using System.Globalization;
using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

/// <summary>
/// Durable execution-aid storage for explicit full rebuilds. This root is
/// independent of the v4/vNext recap truth store and contains no event body,
/// prompt, recap content, epoch boundary, or policy decision.
/// </summary>
public sealed class DerivedRecapRebuildSpoolStore {
    internal const long MaximumCaptureBytes = 64 * 1024;
    internal const long MaximumCheckpointBytes = 64 * 1024;
    internal const long MaximumSealBytes = 128 * 1024;

    private readonly RecapDurableFileSystem _fileSystem;
    private readonly RecapStoreTestHooks _testHooks;
    private readonly string _root;
    private readonly string _locksRoot;
    private readonly string _campaignsRoot;
    private readonly string _quarantineRoot;
    private readonly string _refCampaignsRoot;
    private readonly string _lockPath;

    private DerivedRecapRebuildSpoolStore(
        string sessionRepositoryPath,
        RefId refId,
        RecapStoreTestHooks? testHooks
    ) {
        SessionRepositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(sessionRepositoryPath)
        );
        if (refId == default) {
            throw new ArgumentException(
                "Rebuild spool RefId cannot be default.",
                nameof(refId)
            );
        }
        RefId = refId;
        _testHooks = testHooks ?? new RecapStoreTestHooks();
        _fileSystem = new RecapDurableFileSystem(
            SessionRepositoryPath,
            _testHooks.IoObserver
        );
        _root = Path.Combine(
            SessionRepositoryPath,
            "derived",
            "recap",
            "rebuild",
            "v1"
        );
        _locksRoot = Path.Combine(_root, "locks");
        _campaignsRoot = Path.Combine(_root, "campaigns");
        _quarantineRoot = Path.Combine(_root, "quarantine");
        string refToken = RefId.ToHexString();
        _refCampaignsRoot = Path.Combine(_campaignsRoot, refToken);
        _lockPath = Path.Combine(_locksRoot, $"{refToken}.lock");
    }

    public string SessionRepositoryPath { get; }
    public RefId RefId { get; }

    public static DerivedRecapRebuildSpoolStore Open(
        string sessionRepositoryPath,
        RefId refId
    ) => OpenCore(sessionRepositoryPath, refId, testHooks: null);

    internal static DerivedRecapRebuildSpoolStore OpenForTest(
        string sessionRepositoryPath,
        RefId refId,
        RecapStoreTestHooks testHooks
    ) => OpenCore(sessionRepositoryPath, refId, testHooks);

    public async ValueTask<DerivedRecapRebuildSpoolDescriptor>
        CreateCampaignAsync(
        SessionSelectedLineageAuditCapture capture,
        DerivedRecapRebuildSpoolLimits limits,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(limits);
        if (capture.BranchRefId != RefId) {
            throw new ArgumentException(
                "Rebuild capture belongs to another RefId.",
                nameof(capture)
            );
        }
        EnsureScaffolding();
        await using FileStream writeLock =
            await _fileSystem.AcquireExclusiveLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        CleanupAbandonedStagingDirectories();
        string campaignId = Guid.NewGuid().ToString("N");
        var descriptor = new DerivedRecapRebuildSpoolDescriptor(
            campaignId,
            capture,
            limits
        );
        DerivedRecapRebuildSpoolCodec.ValidateDescriptor(descriptor);
        var checkpoint = new DerivedRecapRebuildSpoolCheckpoint(
            descriptor,
            CommittedPageCount: 0,
            NextAddress: capture.CapturedHead,
            EventCount: 0,
            LogicalPayloadBytes: 0,
            EncodedPageBytes: 0,
            DerivedRecapRebuildSpoolCodec.InitialPageChainSha256
        );

        string staging = Path.Combine(
            _refCampaignsRoot,
            $".{campaignId}.{Guid.NewGuid():N}.tmp"
        );
        string final = CampaignRoot(campaignId);
        try {
            _fileSystem.EnsureDirectoryDurable(staging);
            _fileSystem.EnsureDirectoryDurable(
                Path.Combine(staging, "pages")
            );
            await _fileSystem.WriteFileCreateNewAsync(
                    Path.Combine(staging, "capture.json"),
                    DerivedRecapRebuildSpoolCodec.EncodeCapture(
                        descriptor
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            await _fileSystem.WriteFileCreateNewAsync(
                    Path.Combine(staging, "checkpoint.json"),
                    DerivedRecapRebuildSpoolCodec.EncodeCheckpoint(
                        checkpoint
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
            _fileSystem.MoveDirectoryCreateNew(staging, final);
            _fileSystem.FlushDirectory(_refCampaignsRoot);
            return descriptor;
        }
        catch {
            if (Directory.Exists(staging)) {
                _fileSystem.DeleteDirectoryTree(staging);
            }
            throw;
        }
    }

    public async ValueTask<DerivedRecapRebuildSpoolWriter>
        OpenWriterAsync(
        string campaignId,
        CancellationToken cancellationToken = default
    ) {
        campaignId = DerivedRecapRebuildSpoolCodec
            .ValidateCampaignId(campaignId);
        FileStream writeLock =
            await _fileSystem.AcquireExistingExclusiveWriteLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        try {
            DerivedRecapRebuildSpoolCheckpoint checkpoint =
                ReadAndValidateCheckpoint(campaignId);
            if (File.Exists(SealPath(campaignId))) {
                throw new InvalidOperationException(
                    "Sealed rebuild spool cannot be reopened for writing."
                );
            }
            return new DerivedRecapRebuildSpoolWriter(
                this,
                writeLock,
                checkpoint
            );
        }
        catch {
            await writeLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<
        ISessionSelectedLineageAuditPageSnapshot
    > OpenSealedSnapshotAsync(
        string campaignId,
        CancellationToken cancellationToken = default
    ) {
        campaignId = DerivedRecapRebuildSpoolCodec
            .ValidateCampaignId(campaignId);
        FileStream readLock =
            await _fileSystem.AcquireExistingExclusiveReadLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        try {
            DerivedRecapRebuildSpoolCheckpoint checkpoint =
                ReadAndValidateCheckpoint(campaignId);
            if (!checkpoint.IsCaptureComplete) {
                throw new InvalidDataException(
                    "Unsealed or partial rebuild spool cannot be consumed."
                );
            }
            byte[] sealBytes = ReadBounded(
                SealPath(campaignId),
                MaximumSealBytes
            );
            DerivedRecapRebuildSpoolSeal seal =
                DerivedRecapRebuildSpoolCodec.DecodeSeal(
                    sealBytes,
                    checkpoint
                );
            EnsurePageInventory(
                checkpoint,
                allowCurrentOrphan: false
            );
            return new SealedSnapshot(
                this,
                readLock,
                seal
            );
        }
        catch {
            await readLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DeleteCampaignAsync(
        string campaignId,
        CancellationToken cancellationToken = default
    ) {
        campaignId = DerivedRecapRebuildSpoolCodec
            .ValidateCampaignId(campaignId);
        await using FileStream writeLock =
            await _fileSystem.AcquireExistingExclusiveWriteLockAsync(
                    _lockPath,
                    cancellationToken
                )
                .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        string campaignRoot = CampaignRoot(campaignId);
        if (!Directory.Exists(campaignRoot)) {
            string[] quarantined = FindQuarantinedCampaigns(
                campaignId
            );
            if (quarantined.Length == 1) {
                _fileSystem.DeleteDirectoryTree(quarantined[0]);
                return;
            }
            if (quarantined.Length > 1) {
                throw new InvalidDataException(
                    "Multiple quarantined rebuild campaigns match one campaign id."
                );
            }
            throw new DirectoryNotFoundException(
                $"Rebuild campaign does not exist: {campaignId}"
            );
        }
        string refQuarantine = Path.Combine(
            _quarantineRoot,
            RefId.ToHexString()
        );
        _fileSystem.EnsureDirectoryDurable(refQuarantine);
        string quarantine = Path.Combine(
            refQuarantine,
            $"{campaignId}.{Guid.NewGuid():N}"
        );
        _fileSystem.MoveDirectoryCreateNew(campaignRoot, quarantine);
        _fileSystem.FlushDirectory(_refCampaignsRoot);
        _testHooks.AfterRebuildDeleteQuarantineRename?.Invoke();
        _fileSystem.DeleteDirectoryTree(quarantine);
    }

    internal IEnumerable<SessionSelectedLineageAuditPage>
        ReadCommittedPages(string campaignId, long pageCount) {
        for (long ordinal = 0; ordinal < pageCount; ordinal++) {
            yield return ReadPage(campaignId, ordinal);
        }
    }

    internal async ValueTask<DerivedRecapRebuildSpoolCheckpoint>
        AppendPageUnderLockAsync(
        DerivedRecapRebuildSpoolCheckpoint checkpoint,
        SessionSelectedLineageAuditPage page,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(page);
        if (checkpoint.IsCaptureComplete) {
            throw new InvalidOperationException(
                "Completed rebuild capture cannot append another page."
            );
        }
        DerivedRecapRebuildSpoolCodec.ValidatePage(page);
        if (page.Ordinal != checkpoint.CommittedPageCount
            || page.PageHead != checkpoint.NextAddress
            || page.HeadToOldest.Count
                > checkpoint.Descriptor.Limits.PageEventCount) {
            throw new InvalidDataException(
                "Rebuild page does not match the exact checkpoint continuation."
            );
        }
        byte[] pageBytes = DerivedRecapRebuildSpoolCodec.EncodePage(
            checkpoint.Descriptor.CampaignId,
            page
        );
        if (pageBytes.LongLength
            > checkpoint.Descriptor.Limits.MaximumPageBytes) {
            throw new InvalidDataException(
                "Canonical rebuild page exceeds its per-page byte limit."
            );
        }
        long nextEventCount = checked(
            checkpoint.EventCount + page.HeadToOldest.Count
        );
        long nextLogicalPayloadBytes =
            checkpoint.LogicalPayloadBytes;
        foreach (SessionSelectedLineageAuditEntry entry
                 in page.HeadToOldest) {
            nextLogicalPayloadBytes = checked(
                nextLogicalPayloadBytes
                + entry.LogicalPayloadBytes
            );
        }
        long nextEncodedPageBytes = checked(
            checkpoint.EncodedPageBytes + pageBytes.LongLength
        );
        if (nextEventCount
                > checkpoint.Descriptor.Limits.MaximumEventCount
            || nextEncodedPageBytes
                > checkpoint.Descriptor.Limits
                    .MaximumTotalEncodedBytes) {
            throw new InvalidDataException(
                "Rebuild spool campaign budget would be exceeded."
            );
        }
        string pagePath = PagePath(
            checkpoint.Descriptor.CampaignId,
            page.Ordinal
        );
        if (File.Exists(pagePath)) {
            byte[] orphan = ReadBounded(
                pagePath,
                checkpoint.Descriptor.Limits.MaximumPageBytes
            );
            if (!orphan.AsSpan().SequenceEqual(pageBytes)) {
                throw new InvalidDataException(
                    "Existing orphan rebuild page does not match resumed raw audit."
                );
            }
        }
        else {
            await _fileSystem.WriteFileCreateNewAsync(
                    pagePath,
                    pageBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        _testHooks.AfterRebuildPageInstalledBeforeCheckpoint
            ?.Invoke();
        var next = new DerivedRecapRebuildSpoolCheckpoint(
            checkpoint.Descriptor,
            checked(checkpoint.CommittedPageCount + 1),
            page.Continuation,
            nextEventCount,
            nextLogicalPayloadBytes,
            nextEncodedPageBytes,
            DerivedRecapRebuildSpoolCodec.AdvancePageChain(
                checkpoint.PageChainSha256,
                pageBytes
            )
        );
        byte[] checkpointBytes =
            DerivedRecapRebuildSpoolCodec.EncodeCheckpoint(next);
        await _fileSystem.WriteFileAtomicReplaceAsync(
                CheckpointPath(checkpoint.Descriptor.CampaignId),
                checkpointBytes,
                _testHooks.BeforeRebuildCheckpointReplace,
                afterReplace: null,
                cancellationToken
            )
            .ConfigureAwait(false);
        return next;
    }

    internal async ValueTask SealUnderLockAsync(
        DerivedRecapRebuildSpoolCheckpoint checkpoint,
        SessionSelectedLineageAuditAuthority authority,
        CancellationToken cancellationToken
    ) {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(authority);
        if (!checkpoint.IsCaptureComplete
            || authority.Capture != checkpoint.Descriptor.Capture
            || authority.EventCount != checkpoint.EventCount
            || authority.LogicalPayloadBytes
                != checkpoint.LogicalPayloadBytes) {
            throw new InvalidDataException(
                "Rebuild audit authority does not match the completed spool checkpoint."
            );
        }
        var seal = new DerivedRecapRebuildSpoolSeal(
            checkpoint,
            authority.RootAddress,
            authority.BootstrapSeed.Address,
            authority.BootstrapSeed.Setups,
            authority.HeadSetups,
            authority.ExecutionStateAtCapturedHead.Phase,
            authority.ExecutionStateAtCapturedHead.HeadKind
        );
        byte[] sealBytes =
            DerivedRecapRebuildSpoolCodec.EncodeSeal(seal);
        if (sealBytes.LongLength > MaximumSealBytes) {
            throw new InvalidDataException(
                "Canonical rebuild seal exceeds its byte limit."
            );
        }
        _testHooks.BeforeRebuildSealInstall?.Invoke();
        await _fileSystem.WriteFileCreateNewAsync(
                SealPath(checkpoint.Descriptor.CampaignId),
                sealBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private DerivedRecapRebuildSpoolCheckpoint
        ReadAndValidateCheckpoint(string campaignId) {
        string root = CampaignRoot(campaignId);
        if (!Directory.Exists(root)) {
            throw new DirectoryNotFoundException(
                $"Rebuild campaign does not exist: {campaignId}"
            );
        }
        CleanupCampaignTemporaryFiles(campaignId);
        EnsureCampaignInventory(campaignId);
        DerivedRecapRebuildSpoolDescriptor descriptor =
            DerivedRecapRebuildSpoolCodec.DecodeCapture(
                ReadBounded(
                    CapturePath(campaignId),
                    MaximumCaptureBytes
                )
            );
        if (descriptor.Capture.BranchRefId != RefId
            || !string.Equals(
                descriptor.CampaignId,
                campaignId,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Rebuild capture does not belong to this spool store."
            );
        }
        DerivedRecapRebuildSpoolCheckpoint checkpoint =
            DerivedRecapRebuildSpoolCodec.DecodeCheckpoint(
                ReadBounded(
                    CheckpointPath(campaignId),
                    MaximumCheckpointBytes
                ),
                descriptor
            );
        ValidateCommittedPages(checkpoint);
        EnsurePageInventory(
            checkpoint,
            allowCurrentOrphan: true
        );
        return checkpoint;
    }

    private void ValidateCommittedPages(
        DerivedRecapRebuildSpoolCheckpoint checkpoint
    ) {
        EventAddress? next =
            checkpoint.Descriptor.Capture.CapturedHead;
        long eventCount = 0;
        long logicalPayloadBytes = 0;
        long encodedPageBytes = 0;
        string chain =
            DerivedRecapRebuildSpoolCodec.InitialPageChainSha256;
        for (long ordinal = 0;
             ordinal < checkpoint.CommittedPageCount;
             ordinal++) {
            string path = PagePath(
                checkpoint.Descriptor.CampaignId,
                ordinal
            );
            byte[] bytes = ReadBounded(
                path,
                checkpoint.Descriptor.Limits.MaximumPageBytes
            );
            SessionSelectedLineageAuditPage page =
                DerivedRecapRebuildSpoolCodec.DecodePage(
                    bytes,
                    checkpoint.Descriptor.CampaignId
                );
            if (page.Ordinal != ordinal || page.PageHead != next) {
                throw new InvalidDataException(
                    $"Rebuild spool page {ordinal} breaks checkpoint continuity."
                );
            }
            next = page.Continuation;
            eventCount = checked(
                eventCount + page.HeadToOldest.Count
            );
            foreach (SessionSelectedLineageAuditEntry entry
                     in page.HeadToOldest) {
                logicalPayloadBytes = checked(
                    logicalPayloadBytes
                    + entry.LogicalPayloadBytes
                );
            }
            encodedPageBytes = checked(
                encodedPageBytes + bytes.LongLength
            );
            chain = DerivedRecapRebuildSpoolCodec.AdvancePageChain(
                chain,
                bytes
            );
        }
        if (next != checkpoint.NextAddress
            || eventCount != checkpoint.EventCount
            || logicalPayloadBytes
                != checkpoint.LogicalPayloadBytes
            || encodedPageBytes != checkpoint.EncodedPageBytes
            || !string.Equals(
                chain,
                checkpoint.PageChainSha256,
                StringComparison.Ordinal
            )) {
            throw new InvalidDataException(
                "Rebuild checkpoint does not match its committed page chain."
            );
        }
    }

    private void EnsurePageInventory(
        DerivedRecapRebuildSpoolCheckpoint checkpoint,
        bool allowCurrentOrphan
    ) {
        string campaignId = checkpoint.Descriptor.CampaignId;
        long committedPageCount = checkpoint.CommittedPageCount;
        string pagesRoot = PagesRoot(campaignId);
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     pagesRoot
                 )) {
            _fileSystem.EnsureSafeDescendant(entry);
            if (Directory.Exists(entry)) {
                throw new InvalidDataException(
                    "Rebuild pages directory contains an unexpected directory."
                );
            }
            string fileName = Path.GetFileName(entry);
            if (fileName.StartsWith(".", StringComparison.Ordinal)
                && fileName.EndsWith(".tmp", StringComparison.Ordinal)) {
                continue;
            }
            if (!TryParsePageFileName(fileName, out long ordinal)
                || ordinal > committedPageCount
                || (ordinal == committedPageCount
                    && !allowCurrentOrphan)) {
                throw new InvalidDataException(
                    $"Rebuild pages directory contains unexpected entry '{fileName}'."
                );
            }
            if (ordinal == committedPageCount
                && new FileInfo(entry).Length
                    > checkpoint.Descriptor.Limits.MaximumPageBytes) {
                throw new InvalidDataException(
                    "Orphan rebuild page exceeds its per-page byte limit."
                );
            }
        }
        for (long ordinal = 0;
             ordinal < committedPageCount;
             ordinal++) {
            if (!File.Exists(PagePath(campaignId, ordinal))) {
                throw new InvalidDataException(
                    $"Rebuild spool is missing committed page {ordinal}."
                );
            }
        }
    }

    private void EnsureCampaignInventory(string campaignId) {
        string campaignRoot = CampaignRoot(campaignId);
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     campaignRoot
                 )) {
            _fileSystem.EnsureSafeDescendant(entry);
            string name = Path.GetFileName(entry);
            bool expected = name is "capture.json"
                or "checkpoint.json"
                or "seal.json"
                or "pages";
            if (!expected) {
                throw new InvalidDataException(
                    $"Rebuild campaign contains unexpected entry '{name}'."
                );
            }
            if (name == "pages" && !Directory.Exists(entry)) {
                throw new InvalidDataException(
                    "Rebuild campaign pages entry is not a directory."
                );
            }
            if (name != "pages" && expected && Directory.Exists(entry)) {
                throw new InvalidDataException(
                    $"Rebuild campaign file '{name}' is a directory."
                );
            }
        }
        if (!File.Exists(CapturePath(campaignId))
            || !File.Exists(CheckpointPath(campaignId))
            || !Directory.Exists(PagesRoot(campaignId))) {
            throw new InvalidDataException(
                "Rebuild campaign is missing a required entry."
            );
        }
    }

    private void CleanupCampaignTemporaryFiles(string campaignId) {
        DeleteTemporaryFilesIn(CampaignRoot(campaignId));
        DeleteTemporaryFilesIn(PagesRoot(campaignId));
    }

    private void DeleteTemporaryFilesIn(string directory) {
        bool deleted = false;
        foreach (string entry in Directory.EnumerateFileSystemEntries(
                     directory
                 )) {
            string name = Path.GetFileName(entry);
            if (!name.StartsWith(".", StringComparison.Ordinal)
                || !name.EndsWith(".tmp", StringComparison.Ordinal)) {
                continue;
            }
            _fileSystem.EnsureSafeDescendant(entry);
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0
                || (attributes & FileAttributes.Directory) != 0) {
                throw new InvalidDataException(
                    $"Rebuild temporary entry is unsafe: {entry}"
                );
            }
            File.Delete(entry);
            deleted = true;
        }
        if (deleted) {
            _fileSystem.FlushDirectory(directory);
        }
    }

    private SessionSelectedLineageAuditPage ReadPage(
        string campaignId,
        long ordinal
    ) {
        DerivedRecapRebuildSpoolDescriptor descriptor =
            DerivedRecapRebuildSpoolCodec.DecodeCapture(
                ReadBounded(
                    CapturePath(campaignId),
                    MaximumCaptureBytes
                )
            );
        byte[] bytes = ReadBounded(
            PagePath(campaignId, ordinal),
            descriptor.Limits.MaximumPageBytes
        );
        SessionSelectedLineageAuditPage page =
            DerivedRecapRebuildSpoolCodec.DecodePage(
                bytes,
                campaignId
            );
        if (page.Ordinal != ordinal) {
            throw new InvalidDataException(
                "Rebuild page filename and ordinal differ."
            );
        }
        return page;
    }

    private byte[] ReadBounded(string path, long maxBytes) {
        _fileSystem.EnsureSafeDescendant(path);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
        if (stream.Length > maxBytes || stream.Length > int.MaxValue) {
            throw new InvalidDataException(
                $"Rebuild spool file '{path}' exceeds {maxBytes} bytes."
            );
        }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        _fileSystem.EnsureSafeDescendant(path);
        return bytes;
    }

    private void EnsureScaffolding() {
        _fileSystem.EnsureDirectoryDurable(_locksRoot);
        _fileSystem.EnsureDirectoryDurable(_campaignsRoot);
        _fileSystem.EnsureDirectoryDurable(_quarantineRoot);
        _fileSystem.EnsureDirectoryDurable(_refCampaignsRoot);
    }

    private static DerivedRecapRebuildSpoolStore OpenCore(
        string sessionRepositoryPath,
        RefId refId,
        RecapStoreTestHooks? testHooks
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            sessionRepositoryPath
        );
        if (refId == default) {
            throw new ArgumentException(
                "Rebuild spool requires a non-default RefId.",
                nameof(refId)
            );
        }
        string fullPath = Path.GetFullPath(sessionRepositoryPath);
        RecapDurableFileSystem
            .EnsureExistingPathChainHasNoReparsePoint(fullPath);
        if (!Directory.Exists(fullPath)) {
            throw new DirectoryNotFoundException(
                $"SessionJournal repository does not exist: {fullPath}"
            );
        }
        return new DerivedRecapRebuildSpoolStore(
            fullPath,
            refId,
            testHooks
        );
    }

    private void CleanupAbandonedStagingDirectories() {
        foreach (string entry in Directory.EnumerateDirectories(
                     _refCampaignsRoot
                 )) {
            _fileSystem.EnsureSafeDescendant(entry);
            string name = Path.GetFileName(entry);
            if (name.StartsWith(".", StringComparison.Ordinal)
                && name.EndsWith(".tmp", StringComparison.Ordinal)) {
                _fileSystem.DeleteDirectoryTree(entry);
            }
        }
        string refQuarantine = Path.Combine(
            _quarantineRoot,
            RefId.ToHexString()
        );
        if (!Directory.Exists(refQuarantine)) {
            return;
        }
        foreach (string entry in Directory.EnumerateDirectories(
                     refQuarantine
                 )) {
            _fileSystem.EnsureSafeDescendant(entry);
            _fileSystem.DeleteDirectoryTree(entry);
        }
    }

    private string[] FindQuarantinedCampaigns(string campaignId) {
        string refQuarantine = Path.Combine(
            _quarantineRoot,
            RefId.ToHexString()
        );
        if (!Directory.Exists(refQuarantine)) {
            return [];
        }
        return [
            .. Directory.EnumerateDirectories(refQuarantine)
                .Where(path => Path.GetFileName(path).StartsWith(
                    campaignId + ".",
                    StringComparison.Ordinal
                ))
        ];
    }

    private string CampaignRoot(string campaignId) => Path.Combine(
        _refCampaignsRoot,
        campaignId
    );

    private string CapturePath(string campaignId) => Path.Combine(
        CampaignRoot(campaignId),
        "capture.json"
    );

    private string CheckpointPath(string campaignId) => Path.Combine(
        CampaignRoot(campaignId),
        "checkpoint.json"
    );

    private string SealPath(string campaignId) => Path.Combine(
        CampaignRoot(campaignId),
        "seal.json"
    );

    private string PagesRoot(string campaignId) => Path.Combine(
        CampaignRoot(campaignId),
        "pages"
    );

    private string PagePath(string campaignId, long ordinal) =>
        Path.Combine(
            PagesRoot(campaignId),
            ordinal.ToString("D20", CultureInfo.InvariantCulture)
                + ".json"
        );

    private static bool TryParsePageFileName(
        string fileName,
        out long ordinal
    ) {
        ordinal = -1;
        return fileName.Length == 25
            && fileName.EndsWith(".json", StringComparison.Ordinal)
            && long.TryParse(
                fileName.AsSpan(0, 20),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ordinal
            )
            && ordinal >= 0;
    }

    private sealed class SealedSnapshot
        : ISessionSelectedLineageAuditPageSnapshot {
        private readonly DerivedRecapRebuildSpoolStore _owner;
        private readonly FileStream _lock;
        private bool _disposed;

        public SealedSnapshot(
            DerivedRecapRebuildSpoolStore owner,
            FileStream readLock,
            DerivedRecapRebuildSpoolSeal seal
        ) {
            _owner = owner;
            _lock = readLock;
            Seal = seal;
        }

        public DerivedRecapRebuildSpoolSeal Seal { get; }
        public SessionSelectedLineageAuditCapture Capture =>
            Seal.Checkpoint.Descriptor.Capture;
        public long PageCount =>
            Seal.Checkpoint.CommittedPageCount;

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadHeadToOldestPages() {
            ThrowIfDisposed();
            return _owner.ReadCommittedPages(
                Seal.Checkpoint.Descriptor.CampaignId,
                PageCount
            );
        }

        public IEnumerable<SessionSelectedLineageAuditPage>
            ReadOldestToHeadPages() {
            ThrowIfDisposed();
            return ReadReverse();
        }

        public void Dispose() {
            if (_disposed) {
                return;
            }
            _lock.Dispose();
            _disposed = true;
        }

        private IEnumerable<SessionSelectedLineageAuditPage>
            ReadReverse() {
            for (long ordinal = PageCount - 1;
                 ordinal >= 0;
                 ordinal--) {
                yield return _owner.ReadPage(
                    Seal.Checkpoint.Descriptor.CampaignId,
                    ordinal
                );
            }
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
