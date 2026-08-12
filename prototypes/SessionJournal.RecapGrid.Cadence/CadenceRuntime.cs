using Atelia.EventJournal;
using Atelia.SessionJournal.HistoryTimeline;

namespace Atelia.SessionJournal.RecapGrid.Cadence;

public static class RecapGridCadenceFactory {
    public static RecapGridCadenceCreateResult Create(
        SessionJournalEngine mutableOwner,
        RecapGridCadencePolicySpec policy
    ) => CreateWithHooks(mutableOwner, policy,
        CadencePersistenceTestHooks.None);

    internal static RecapGridCadenceCreateResult CreateWithHooks(
        SessionJournalEngine mutableOwner,
        RecapGridCadencePolicySpec policy,
        CadencePersistenceTestHooks hooks
    ) {
        try {
            ArgumentNullException.ThrowIfNull(mutableOwner);
            ArgumentNullException.ThrowIfNull(policy);
            ArgumentNullException.ThrowIfNull(hooks);
            return mutableOwner.ExecuteDerivedSidecarMutation(
                "RecapGridCadence.Create",
                readView => CreateUnderOwner(
                    PathsFrom(readView),
                    policy,
                    hooks));
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return MapCreate(exception);
        }
    }

    private static RecapGridCadenceCreateResult CreateUnderOwner(
        CadencePaths paths,
        RecapGridCadencePolicySpec policy,
        CadencePersistenceTestHooks hooks
    ) {
            using CadenceDirectoryLease directory =
                LinuxCadenceFiles.OpenDirectory(
                    paths, create: true, hooks);
            LinuxCadenceFiles.EnsureSlots(paths, directory);
            using FileStream lease = LinuxCadenceFiles.AcquireLock(
                directory, exclusive: true, paths.LockPath);
            if (LinuxCadenceFiles.EntryExists(
                    directory, CadencePaths.StateName, paths.StatePath)) {
                RecapGridCadenceSnapshot existing = ReadExact(
                    paths, directory);
                return new RecapGridCadenceCreateResult.AlreadyExists(existing);
            }
            RecapGridCadenceSnapshot intended = CadenceCanonicalCodec.Create(
                paths.RefId, generation: 0, policy);
            try {
                LinuxCadenceFiles.WriteAtomic(paths, directory,
                    intended.ToCanonicalBytes(), createNew: true, hooks);
            }
            catch (CadencePublishIndeterminateException) {
                return new RecapGridCadenceCreateResult.CommitIndeterminate(
                    intended.Head, ObserveHead(paths));
            }
            return new RecapGridCadenceCreateResult.Created(intended);
    }

    public static RecapGridCadenceOpenResult OpenMutable(
        SessionJournalEngine mutableOwner
    ) => OpenMutableForTest(mutableOwner,
        CadencePersistenceTestHooks.None);

    internal static RecapGridCadenceOpenResult OpenMutableForTest(
        SessionJournalEngine mutableOwner,
        CadencePersistenceTestHooks hooks
    ) {
        try {
            ArgumentNullException.ThrowIfNull(mutableOwner);
            ArgumentNullException.ThrowIfNull(hooks);
            CadencePaths paths = PathsFromMutable(mutableOwner);
            RecapGridCadenceReader reader = OpenReaderCore(
                paths, hooks, out CadenceLifetime lifetime);
            return new RecapGridCadenceOpenResult.Opened(
                new RecapGridCadenceHandle(
                    reader,
                    new RecapGridCadenceCoordinator(
                        paths, lifetime, hooks, mutableOwner),
                    lifetime,
                    paths,
                    mutableOwner));
        }
        catch (CadenceDirectoryAbsentException) {
            return new RecapGridCadenceOpenResult.Absent();
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return MapOpen(exception);
        }
    }

    public static RecapGridCadenceReaderOpenResult OpenReader(
        SessionJournalReadView readView
    ) {
        try {
            ArgumentNullException.ThrowIfNull(readView);
            var paths = PathsFrom(readView);
            RecapGridCadenceReader reader = OpenReaderCore(
                paths,
                CadencePersistenceTestHooks.None,
                out CadenceLifetime lifetime);
            return new RecapGridCadenceReaderOpenResult.Opened(
                new RecapGridCadenceReaderHandle(reader, lifetime));
        }
        catch (CadenceDirectoryAbsentException) {
            return new RecapGridCadenceReaderOpenResult.Absent();
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return MapReaderOpen(exception);
        }
    }

    internal static RecapGridCadenceReaderOpenResult OpenForMaintenance(
        string repositoryPath,
        RefId refId
    ) {
        try {
            var paths = new CadencePaths(repositoryPath, refId);
            ValidateRefForMaintenance(paths);
            RecapGridCadenceReader reader = OpenReaderCore(
                paths,
                CadencePersistenceTestHooks.None,
                out CadenceLifetime lifetime);
            return new RecapGridCadenceReaderOpenResult.Opened(
                new RecapGridCadenceReaderHandle(reader, lifetime));
        }
        catch (CadenceDirectoryAbsentException) {
            return new RecapGridCadenceReaderOpenResult.Absent();
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return MapReaderOpen(exception);
        }
    }

    private static RecapGridCadenceReader OpenReaderCore(
        CadencePaths paths,
        CadencePersistenceTestHooks hooks,
        out CadenceLifetime lifetime
    ) {
        using CadenceDirectoryLease directory =
            LinuxCadenceFiles.OpenDirectory(paths, create: false, hooks);
        if (!LinuxCadenceFiles.EntryExists(
                directory, CadencePaths.StateName, paths.StatePath)) {
            throw new CadenceDirectoryAbsentException();
        }
        if (!LinuxCadenceFiles.EntryExists(
                directory, CadencePaths.LockName, paths.LockPath)) {
            throw new CadenceStoreException(
                "CadenceLockAbsent",
                "The canonical Cadence state exists without its lock slot.");
        }
        using (FileStream lease = LinuxCadenceFiles.AcquireLock(
                   directory, exclusive: false, paths.LockPath)) {
            _ = ReadExact(paths, directory);
        }
        lifetime = new CadenceLifetime();
        return new RecapGridCadenceReader(paths, lifetime);
    }

    internal static RecapGridCadenceSnapshot ReadExact(
        CadencePaths paths,
        CadenceDirectoryLease directory
    ) {
        RecapGridCadenceSnapshot snapshot = CadenceCanonicalCodec.Decode(
            LinuxCadenceFiles.ReadBounded(directory, paths.StatePath));
        if (snapshot.Head.RefId != paths.RefId) {
            throw new CadenceStoreException(
                "CadenceRefMismatch",
                "The canonical Cadence state belongs to another Ref.");
        }
        return snapshot;
    }

    internal static RecapGridCadenceHeadRef? ObserveHead(CadencePaths paths) {
        try {
            using CadenceDirectoryLease directory =
                LinuxCadenceFiles.OpenDirectory(
                    paths, create: false,
                    CadencePersistenceTestHooks.None);
            return LinuxCadenceFiles.EntryExists(
                    directory, CadencePaths.StateName, paths.StatePath)
                ? ReadExact(paths, directory).Head
                : null;
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return null;
        }
    }

    private static CadencePaths PathsFrom(SessionJournalReadView readView) {
        // Reading both values through the same owner-bound view avoids opening a
        // second EventJournal owner and therefore remains valid while a mutable
        // SessionJournalEngine owns the repository.
        string repositoryPath = readView.Path;
        RefId refId = readView.BranchRefId;
        return new CadencePaths(repositoryPath, refId);
    }

    private static CadencePaths PathsFromMutable(
        SessionJournalEngine mutableOwner
    ) {
        ArgumentNullException.ThrowIfNull(mutableOwner);
        if (mutableOwner.IsReadOnly) {
            throw new CadenceStoreException(
                "CadenceMutableOwnerRequired",
                "Cadence mutation requires a mutable SessionJournal owner.");
        }
        _ = mutableOwner.ReadCurrentHead();
        return new CadencePaths(
            mutableOwner.Path,
            mutableOwner.BranchRefId);
    }

    internal static void RequireMutableOwner(
        SessionJournalEngine mutableOwner,
        CadencePaths paths
    ) {
        if (mutableOwner.IsReadOnly) {
            throw new CadenceStoreException(
                "CadenceMutableOwnerRequired",
                "Cadence mutation requires a mutable SessionJournal owner.");
        }
        _ = mutableOwner.ReadCurrentHead();
        if (mutableOwner.BranchRefId != paths.RefId
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(mutableOwner.Path)),
                paths.RepositoryPath,
                StringComparison.Ordinal)) {
            throw new CadenceStoreException(
                "CadenceMutableOwnerScopeMismatch",
                "The mutable SessionJournal owner no longer matches the Cadence scope.");
        }
    }

    internal static void ValidateRefForMaintenance(CadencePaths paths) {
        using EventJournal.EventJournal journal =
            EventJournal.EventJournal.OpenReadOnlyExisting(paths.RepositoryPath);
        _ = journal.GetHead(paths.RefId);
    }

    internal static RecapGridCadenceCreateResult MapCreate(Exception exception)
        => exception switch {
            PlatformNotSupportedException
                => new RecapGridCadenceCreateResult.PlatformUnsupported(),
            SessionJournalConcurrentMutationException
                => new RecapGridCadenceCreateResult.Busy(),
            CadenceBusyException
                => new RecapGridCadenceCreateResult.Busy(),
            CadenceUnsupportedSchemaException schema
                => new RecapGridCadenceCreateResult.UnsupportedSchema(
                    schema.Version),
            CadenceStoreException invalid
                => new RecapGridCadenceCreateResult.Invalid(
                    invalid.Code, invalid.Message),
            _ => new RecapGridCadenceCreateResult.Invalid(
                "CadenceCreateInvalid", exception.Message)
        };

    internal static RecapGridCadenceOpenResult MapOpen(Exception exception)
        => exception switch {
            PlatformNotSupportedException
                => new RecapGridCadenceOpenResult.PlatformUnsupported(),
            CadenceBusyException
                => new RecapGridCadenceOpenResult.Busy(),
            CadenceUnsupportedSchemaException schema
                => new RecapGridCadenceOpenResult.UnsupportedSchema(
                    schema.Version),
            CadenceStoreException invalid
                => new RecapGridCadenceOpenResult.Invalid(
                    invalid.Code, invalid.Message),
            _ => new RecapGridCadenceOpenResult.Invalid(
                "CadenceOpenInvalid", exception.Message)
        };

    internal static RecapGridCadenceReaderOpenResult MapReaderOpen(
        Exception exception
    ) => exception switch {
        PlatformNotSupportedException
            => new RecapGridCadenceReaderOpenResult.PlatformUnsupported(),
        CadenceBusyException
            => new RecapGridCadenceReaderOpenResult.Busy(),
        CadenceUnsupportedSchemaException schema
            => new RecapGridCadenceReaderOpenResult.UnsupportedSchema(
                schema.Version),
        CadenceStoreException invalid
            => new RecapGridCadenceReaderOpenResult.Invalid(
                invalid.Code, invalid.Message),
        _ => new RecapGridCadenceReaderOpenResult.Invalid(
            "CadenceOpenInvalid", exception.Message)
    };
}

public sealed class RecapGridCadenceHandle : IDisposable {
    private readonly CadenceLifetime _lifetime;
    private readonly CadencePaths _paths;
    private readonly SessionJournalEngine _mutableOwner;
    internal RecapGridCadenceHandle(
        RecapGridCadenceReader reader,
        RecapGridCadenceCoordinator coordinator,
        CadenceLifetime lifetime,
        CadencePaths paths,
        SessionJournalEngine mutableOwner
    ) {
        Reader = reader;
        Coordinator = coordinator;
        _lifetime = lifetime;
        _paths = paths;
        _mutableOwner = mutableOwner;
    }
    public RecapGridCadenceReader Reader { get; }
    public RecapGridCadenceCoordinator Coordinator { get; }
    public RecapGridCadenceTimelineSealOpenResult BeginTimelineSeal(
        HistoryTimelineHandle timeline
    ) => RecapGridCadenceTimelineSeal.Open(this, timeline);
    internal CadenceLifetime Lifetime => _lifetime;
    internal CadencePaths Paths => _paths;
    internal SessionJournalEngine MutableOwner => _mutableOwner;
    public void Dispose() => _lifetime.Dispose();
}

public sealed class RecapGridCadenceReaderHandle : IDisposable {
    private readonly CadenceLifetime _lifetime;
    internal RecapGridCadenceReaderHandle(
        RecapGridCadenceReader reader,
        CadenceLifetime lifetime
    ) {
        Reader = reader;
        _lifetime = lifetime;
    }
    public RecapGridCadenceReader Reader { get; }
    public void Dispose() => _lifetime.Dispose();
}

public sealed class RecapGridCadenceReader {
    private readonly CadencePaths _paths;
    private readonly CadenceLifetime _lifetime;
    internal RecapGridCadenceReader(
        CadencePaths paths,
        CadenceLifetime lifetime
    ) {
        _paths = paths;
        _lifetime = lifetime;
    }

    public RecapGridCadenceReadResult ReadSnapshot() {
        using CadenceLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridCadenceReadResult.Disposed();
        }
        try {
            using CadenceDirectoryLease directory =
                LinuxCadenceFiles.OpenDirectory(
                    _paths, create: false,
                    CadencePersistenceTestHooks.None);
            using FileStream lease = LinuxCadenceFiles.AcquireLock(
                directory, exclusive: false, _paths.LockPath);
            return new RecapGridCadenceReadResult.Available(
                RecapGridCadenceFactory.ReadExact(_paths, directory));
        }
        catch (CadenceBusyException) {
            return new RecapGridCadenceReadResult.Busy();
        }
        catch (CadenceUnsupportedSchemaException schema) {
            return new RecapGridCadenceReadResult.UnsupportedSchema(
                schema.Version);
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return exception is CadenceStoreException invalid
                ? new RecapGridCadenceReadResult.Invalid(
                    invalid.Code, invalid.Message)
                : new RecapGridCadenceReadResult.Invalid(
                    "CadenceReadInvalid", exception.Message);
        }
    }
}

public sealed class RecapGridCadenceCoordinator {
    private readonly CadencePaths _paths;
    private readonly CadenceLifetime _lifetime;
    private readonly CadencePersistenceTestHooks _hooks;
    private readonly SessionJournalEngine _mutableOwner;
    internal RecapGridCadenceCoordinator(
        CadencePaths paths,
        CadenceLifetime lifetime,
        CadencePersistenceTestHooks hooks,
        SessionJournalEngine mutableOwner
    ) {
        _paths = paths;
        _lifetime = lifetime;
        _hooks = hooks;
        _mutableOwner = mutableOwner;
    }

    public RecapGridCadenceCompareExchangeResult CompareExchangePolicy(
        RecapGridCadenceHeadRef expected,
        RecapGridCadencePolicySpec policy
    ) {
        using CadenceLifetime.Operation? operation = _lifetime.TryEnter();
        if (operation is null) {
            return new RecapGridCadenceCompareExchangeResult.Disposed();
        }
        try {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(policy);
            if (expected.RefId != _paths.RefId) {
                return new RecapGridCadenceCompareExchangeResult.Invalid(
                    "CadenceExpectedRefMismatch",
                    "The expected Cadence head belongs to another Ref.");
            }
            return _mutableOwner.ExecuteDerivedSidecarMutation(
                "RecapGridCadence.CompareExchangePolicy",
                readView => CompareExchangeUnderOwner(
                    new CadencePaths(readView.Path, readView.BranchRefId),
                    expected,
                    policy));
        }
        catch (CadenceBusyException) {
            return new RecapGridCadenceCompareExchangeResult.Busy();
        }
        catch (SessionJournalConcurrentMutationException) {
            return new RecapGridCadenceCompareExchangeResult.Busy();
        }
        catch (CadenceUnsupportedSchemaException schema) {
            return new RecapGridCadenceCompareExchangeResult
                .UnsupportedSchema(schema.Version);
        }
        catch (Exception exception) when (!CadenceError.IsFatal(exception)) {
            return exception is CadenceStoreException invalid
                ? new RecapGridCadenceCompareExchangeResult.Invalid(
                    invalid.Code, invalid.Message)
                : new RecapGridCadenceCompareExchangeResult.Invalid(
                    "CadenceCompareExchangeInvalid", exception.Message);
        }
    }

    private RecapGridCadenceCompareExchangeResult CompareExchangeUnderOwner(
        CadencePaths ownerPaths,
        RecapGridCadenceHeadRef expected,
        RecapGridCadencePolicySpec policy
    ) {
            if (ownerPaths.RefId != _paths.RefId
                || !string.Equals(
                    ownerPaths.RepositoryPath,
                    _paths.RepositoryPath,
                    StringComparison.Ordinal)) {
                return new RecapGridCadenceCompareExchangeResult.Invalid(
                    "CadenceMutableOwnerScopeMismatch",
                    "The mutable SessionJournal owner no longer matches the Cadence scope.");
            }
            using CadenceDirectoryLease directory =
                LinuxCadenceFiles.OpenDirectory(
                    _paths, create: false, _hooks);
            using FileStream lease = LinuxCadenceFiles.AcquireLock(
                directory, exclusive: true, _paths.LockPath);
            RecapGridCadenceSnapshot actual =
                RecapGridCadenceFactory.ReadExact(_paths, directory);
            if (actual.Head != expected) {
                return new RecapGridCadenceCompareExchangeResult.Stale(
                    actual.Head);
            }
            RecapGridCadenceSnapshot desired = CadenceCanonicalCodec.Create(
                _paths.RefId,
                checked(actual.Head.Generation + 1),
                policy);
            if (desired.Head.DomainDigest == actual.Head.DomainDigest) {
                return new RecapGridCadenceCompareExchangeResult.Unchanged(
                    actual);
            }
            try {
                LinuxCadenceFiles.WriteAtomic(_paths, directory,
                    desired.ToCanonicalBytes(), createNew: false, _hooks);
            }
            catch (CadencePublishIndeterminateException) {
                return new RecapGridCadenceCompareExchangeResult
                    .CommitIndeterminate(
                        desired.Head,
                        RecapGridCadenceFactory.ObserveHead(_paths));
            }
            return new RecapGridCadenceCompareExchangeResult.Updated(desired);
    }
}

internal sealed class CadenceLifetime : IDisposable {
    private readonly object _gate = new();
    private int _active;
    private bool _closing;
    private bool _disposed;

    internal Operation? TryEnter() {
        lock (_gate) {
            if (_closing) {
                return null;
            }
            _active++;
            return new Operation(this);
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _closing = true;
            while (_active != 0) {
                Monitor.Wait(_gate);
            }
            _disposed = true;
        }
    }

    private void Exit() {
        lock (_gate) {
            _active--;
            if (_closing && _active == 0) {
                Monitor.PulseAll(_gate);
            }
        }
    }

    internal sealed class Operation(CadenceLifetime owner) : IDisposable {
        private CadenceLifetime? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}
