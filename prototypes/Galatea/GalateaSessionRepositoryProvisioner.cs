using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Atelia.SessionJournal;
using Atelia.SessionJournal.HistoryTimeline;
using Atelia.SessionJournal.RecapGrid.Cadence;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Getter;
using Atelia.SessionJournal.RecapGrid.Store;

namespace Atelia.Galatea.Server;

internal static class GalateaSessionRepositoryProvisioner {
    private const int AtCurrentWorkingDirectory = -100;
    private const uint RenameNoReplace = 1;
    private const int ErrorInvalidArgument = 22;
    private const int ErrorFunctionNotImplemented = 38;
    private const int ErrorOperationNotSupported = 95;

    internal static SessionJournalEngine CreateAndPublish(
        string finalPath,
        SessionCreateOptions options,
        RecapGridControlAdmission admission,
        GalateaSessionProvisioningTestHooks? hooks = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(admission);
        if ((admission.Permissions & RecapGridControlPermission.Create)
                != RecapGridControlPermission.Create) {
            throw new InvalidOperationException(
                "The current Agent Control profile does not authorize "
                + "Control creation."
            );
        }

        string normalizedFinalPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(finalPath)
        );
        string parentPath = Path.GetDirectoryName(normalizedFinalPath)
            ?? throw new InvalidOperationException(
                $"Cannot determine SessionJournal parent path: "
                + normalizedFinalPath
            );
        Directory.CreateDirectory(parentPath);
        string stagingPath = Path.Combine(
            parentPath,
            $".galatea-session-{Guid.NewGuid():N}.staging"
        );
        var ownership = new CandidateOwnership();
        SessionJournalEngine? candidate = null;
        bool candidateDisposeAttempted = false;
        bool candidateDisposed = false;
        bool published = false;
        Exception? primaryFailure = null;
        Exception? disposeFailure = null;
        try {
            candidate = SessionJournalEngine.Create(stagingPath, options);
            BootstrapAndValidate(
                candidate,
                admission,
                hooks,
                ownership
            );
            candidateDisposeAttempted = true;
            DisposeCandidate(candidate, hooks);
            candidateDisposed = true;

            hooks?.BeforeSessionRepositoryPublish?.Invoke(
                stagingPath,
                normalizedFinalPath
            );
            PublishNoReplace(stagingPath, normalizedFinalPath);
            published = true;
            return SessionJournalEngine.Open(normalizedFinalPath);
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
            primaryFailure = exception;
        }
        finally {
            if (candidate is not null && !candidateDisposeAttempted) {
                candidateDisposeAttempted = true;
                try {
                    DisposeCandidate(candidate, hooks);
                    candidateDisposed = true;
                }
                catch (Exception exception) when (
                    GalateaExceptionClassifier.IsNonFatal(exception)) {
                    ownership.AllHandlesClosed = false;
                    disposeFailure = exception;
                }
            }
            if (candidateDisposed
                && ownership.AllHandlesClosed
                && !published) {
                TryDeleteOwnedCandidate(stagingPath);
            }
        }
        if (primaryFailure is not null && disposeFailure is not null) {
            throw new AggregateException(
                "Galatea session candidate initialization and disposal "
                + "both failed.",
                primaryFailure,
                disposeFailure
            );
        }
        if (primaryFailure is not null) {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
        if (disposeFailure is not null) {
            ExceptionDispatchInfo.Capture(disposeFailure).Throw();
        }
        throw new InvalidOperationException(
            "Galatea session provisioning exited without a result."
        );
    }

    private static void DisposeCandidate(
        SessionJournalEngine candidate,
        GalateaSessionProvisioningTestHooks? hooks
    ) {
        if (hooks?.DisposeSessionRepositoryCandidate is { } dispose) {
            dispose(candidate);
            return;
        }
        candidate.Dispose();
    }

    private static void BootstrapAndValidate(
        SessionJournalEngine candidate,
        RecapGridControlAdmission admission,
        GalateaSessionProvisioningTestHooks? hooks,
        CandidateOwnership ownership
    ) {
        RecapGridCadenceCreateResult cadence =
            RecapGridCadenceFactory.Create(
                candidate,
                GalateaFirstTurnBootstrapPolicy.Cadence
            );
        RequireCreated("Cadence", cadence);
        hooks?.AfterSessionRepositoryBootstrapStep?.Invoke(
            "Cadence",
            candidate.Path
        );

        var estimator = new O200kBaseHistoryUnitLoadEstimator();
        HistoryTimelineCreateResult timeline =
            HistoryTimelineFactory.Create(
                candidate.ReadView,
                GalateaFirstTurnBootstrapPolicy.CreateTimelinePolicy(),
                estimator
            );
        RequireCreated("Timeline", timeline);
        hooks?.AfterSessionRepositoryBootstrapStep?.Invoke(
            "Timeline",
            candidate.Path
        );

        RecapGridControlCreateResult control =
            RecapGridControlFactory.Create(
                candidate.Path,
                candidate.BranchRefId,
                admission
            );
        RequireCreated("Control", control);
        hooks?.AfterSessionRepositoryBootstrapStep?.Invoke(
            "Control",
            candidate.Path
        );

        ValidateCandidate(candidate, estimator, ownership);
    }

    private static void ValidateCandidate(
        SessionJournalEngine candidate,
        O200kBaseHistoryUnitLoadEstimator estimator,
        CandidateOwnership ownership
    ) {
        SessionExecutionBoundaryInspection boundary =
            candidate.InspectExecutionBoundary();
        if (boundary.Phase != SessionExecutionPhase.Idle
            || boundary.Head is not { } rawHead) {
            throw InvalidCandidate("raw bootstrap is not exactly Idle");
        }
        SessionCurrentLineageSnapshot lineage =
            candidate.ReadCurrentLineageHeaders();
        SessionEventKind[] expectedKinds = [
            SessionEventKind.SessionCreated,
            SessionEventKind.SystemPromptSetup,
            SessionEventKind.RuntimeConfigSetup
        ];
        if (lineage.CapturedHead != rawHead
            || lineage.HeadToRoot.Count != expectedKinds.Length
            || !lineage.HeadToRoot.Select(static value => value.Kind)
                .SequenceEqual(expectedKinds)) {
            throw InvalidCandidate(
                "raw bootstrap does not contain exactly three setup events"
            );
        }

        ValidateCadence(candidate, ownership);
        TimelineHeadRef timelineHead = ValidateTimeline(
            candidate,
            estimator,
            ownership
        );
        ValidateControl(candidate, timelineHead, ownership);
        RecapGridStoreReaderOpenResult store =
            RecapGridStoreFactory.OpenReader(candidate.Path);
        if (store is RecapGridStoreReaderOpenResult.Opened openedStore) {
            DisposeHandle(openedStore.Handle, ownership);
            throw InvalidCandidate("RecapGrid Store is not absent");
        }
        if (store is not RecapGridStoreReaderOpenResult.Absent) {
            throw InvalidCandidate(
                $"Store validation returned {store.GetType().Name}"
            );
        }
        ValidateGetter(candidate, rawHead, estimator, ownership);
    }

    private static void ValidateCadence(
        SessionJournalEngine candidate,
        CandidateOwnership ownership
    ) {
        RecapGridCadenceReaderOpenResult opened =
            RecapGridCadenceFactory.OpenReader(candidate.ReadView);
        if (opened is not RecapGridCadenceReaderOpenResult.Opened available) {
            throw InvalidCandidate(
                $"Cadence validation returned {opened.GetType().Name}"
            );
        }
        RecapGridCadenceReaderHandle handle = available.Handle;
        try {
            RecapGridCadenceReadResult read = handle.Reader.ReadSnapshot();
            if (read is not RecapGridCadenceReadResult.Available snapshot
                || snapshot.Snapshot.Head.RefId != candidate.BranchRefId
                || snapshot.Snapshot.Head.Generation != 0
                || !GalateaFirstTurnBootstrapPolicy.Matches(
                    snapshot.Snapshot.Policy
                )) {
                throw InvalidCandidate("Cadence policy is not exact");
            }
        }
        finally {
            DisposeHandle(handle, ownership);
        }
    }

    private static TimelineHeadRef ValidateTimeline(
        SessionJournalEngine candidate,
        O200kBaseHistoryUnitLoadEstimator estimator,
        CandidateOwnership ownership
    ) {
        HistoryTimelineOpenResult opened = HistoryTimelineFactory.Open(
            candidate.ReadView,
            estimator
        );
        if (opened is not HistoryTimelineOpenResult.Opened available) {
            throw InvalidCandidate(
                $"Timeline validation returned {opened.GetType().Name}"
            );
        }
        HistoryTimelineHandle handle = available.Handle;
        try {
            HistoryTimelineSnapshotResult read =
                handle.Reader.ReadSnapshot();
            if (read is not HistoryTimelineSnapshotResult.Available snapshot
                || snapshot.Head.RefId != candidate.BranchRefId
                || snapshot.Head.HeadRowId is not null
                || snapshot.Head.SelectedRawHeadAtCommit is not null
                || snapshot.Head.SelectedPathCount != 0
                || snapshot.Head.Generation != 0) {
                throw InvalidCandidate("Timeline is not exact and empty");
            }
            PartitionPolicyRevision expected =
                GalateaFirstTurnBootstrapPolicy.CreateTimelinePolicy(
                    snapshot.Head.TimelineId
                );
            if (!string.Equals(
                    expected.PolicyDigest,
                    snapshot.Head.ActivePartitionPolicyDigest,
                    StringComparison.Ordinal)) {
                throw InvalidCandidate("Timeline policy is not exact");
            }
            return snapshot.Head;
        }
        finally {
            DisposeHandle(handle, ownership);
        }
    }

    private static void ValidateControl(
        SessionJournalEngine candidate,
        TimelineHeadRef timelineHead,
        CandidateOwnership ownership
    ) {
        RecapGridControlReaderOpenResult opened =
            RecapGridControlFactory.OpenReader(
                candidate.Path,
                candidate.BranchRefId
            );
        if (opened is not RecapGridControlReaderOpenResult.Opened available) {
            throw InvalidCandidate(
                $"Control validation returned {opened.GetType().Name}"
            );
        }
        RecapGridControlReaderHandle handle = available.Handle;
        try {
            RecapGridControlSnapshotResult read =
                handle.Reader.ReadSnapshot();
            if (read is not RecapGridControlSnapshotResult.Available snapshot
                || snapshot.Snapshot.Head.RefId != candidate.BranchRefId
                || snapshot.Snapshot.Head.TimelineId
                    != timelineHead.TimelineId
                || snapshot.Snapshot.Head.Generation != 0
                || snapshot.Snapshot.Head.ActiveRecipeDigest is not null
                || snapshot.Snapshot.Families.Count != 0
                || snapshot.Snapshot.Definitions.Count != 0
                || snapshot.Snapshot.Recipes.Count != 0) {
                throw InvalidCandidate("Control is not exact and empty");
            }
        }
        finally {
            DisposeHandle(handle, ownership);
        }
    }

    private static void ValidateGetter(
        SessionJournalEngine candidate,
        Atelia.EventJournal.EventAddress rawHead,
        O200kBaseHistoryUnitLoadEstimator estimator,
        CandidateOwnership ownership
    ) {
        RecapGridContextOpenResult opened = RecapGridContextFactory.Open(
            candidate.ReadView,
            estimator
        );
        if (opened is not RecapGridContextOpenResult.Opened available) {
            throw InvalidCandidate(
                $"Getter validation returned {opened.GetType().Name}"
            );
        }
        RecapGridContextHandle handle = available.Handle;
        try {
            if (handle.Resolve(rawHead, nthPrevious: 0)
                    is not RecapGridContextResolveResult
                        .RawHistoryAuthorized) {
                throw InvalidCandidate(
                    "Getter did not authorize the exact raw head"
                );
            }
        }
        finally {
            DisposeHandle(handle, ownership);
        }
    }

    private static void DisposeHandle(
        IDisposable handle,
        CandidateOwnership ownership
    ) {
        try {
            handle.Dispose();
        }
        catch {
            ownership.AllHandlesClosed = false;
            throw;
        }
    }

    private static void RequireCreated(string component, object result) {
        bool created = result switch {
            RecapGridCadenceCreateResult.Created => true,
            HistoryTimelineCreateResult.Created => true,
            RecapGridControlCreateResult.Created => true,
            _ => false
        };
        if (!created) {
            throw InvalidCandidate(
                $"{component} create returned {result.GetType().Name}"
            );
        }
    }

    private static InvalidOperationException InvalidCandidate(string detail)
        => new($"Galatea first-turn bootstrap candidate is invalid: {detail}.");

    private static void PublishNoReplace(
        string stagingPath,
        string finalPath
    ) {
        if (!OperatingSystem.IsLinux()) {
            throw UnsupportedPlatform();
        }
        try {
            if (RenameAt2(
                    AtCurrentWorkingDirectory,
                    stagingPath,
                    AtCurrentWorkingDirectory,
                    finalPath,
                    RenameNoReplace
                ) == 0) {
                return;
            }
            int error = Marshal.GetLastPInvokeError();
            if (error is ErrorInvalidArgument
                or ErrorFunctionNotImplemented
                or ErrorOperationNotSupported) {
                throw UnsupportedPlatform();
            }
            throw new IOException(
                "Atomic create-only SessionJournal publication failed: "
                + $"'{stagingPath}' -> '{finalPath}'.",
                new Win32Exception(error)
            );
        }
        catch (EntryPointNotFoundException exception) {
            throw UnsupportedPlatform(exception);
        }
        catch (DllNotFoundException exception) {
            throw UnsupportedPlatform(exception);
        }
    }

    private static PlatformNotSupportedException UnsupportedPlatform(
        Exception? innerException = null
    ) => new(
        "Galatea create-if-missing requires Linux renameat2 with "
        + "RENAME_NOREPLACE.",
        innerException
    );

    private static void TryDeleteOwnedCandidate(string stagingPath) {
        try {
            if (Directory.Exists(stagingPath)) {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
        catch (Exception exception) when (
            GalateaExceptionClassifier.IsNonFatal(exception)) {
        }
    }

    [DllImport(
        "libc",
        EntryPoint = "renameat2",
        SetLastError = true,
        CharSet = CharSet.Ansi
    )]
    private static extern int RenameAt2(
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath,
        uint flags
    );

    private sealed class CandidateOwnership {
        internal bool AllHandlesClosed { get; set; } = true;
    }
}

internal sealed record GalateaSessionProvisioningTestHooks(
    Action<string, string>? BeforeSessionRepositoryPublish = null,
    Action<string, string>? AfterSessionRepositoryBootstrapStep = null,
    Action<SessionJournalEngine>? DisposeSessionRepositoryCandidate = null
);

internal static class GalateaFirstTurnBootstrapPolicy {
    internal const long MinimumRecentHistoryLoad = 24_000;
    internal const long TargetHistoryLoad = 60_000;
    internal const int MaximumRawEvents = 65_536;
    internal const int MaximumRenderedBytes = 1_048_576;

    internal static RecapGridCadencePolicySpec Cadence { get; } = new(
        MinimumRecentHistoryLoad,
        HistoryPartitionAlgorithms.FirstReplaySafeBoundaryAtTargetV1,
        O200kBaseHistoryUnitLoadEstimator.EstimatorId,
        TargetHistoryLoad,
        MaximumRawEvents,
        MaximumRenderedBytes
    );

    internal static HistoryTimelineInitialPolicySpec CreateTimelinePolicy()
        => new(
            Cadence.PartitionAlgorithmId,
            Cadence.HistoryLoadEstimatorId,
            new HistoryLoadUnit(Cadence.TargetHistoryLoad),
            Cadence.MaxRawEvents,
            Cadence.MaxRenderedBytes
        );

    internal static PartitionPolicyRevision CreateTimelinePolicy(
        TimelineId timelineId
    ) => PartitionPolicyRevision.Create(
        timelineId,
        Cadence.PartitionAlgorithmId,
        Cadence.HistoryLoadEstimatorId,
        new HistoryLoadUnit(Cadence.TargetHistoryLoad),
        Cadence.MaxRawEvents,
        Cadence.MaxRenderedBytes
    );

    internal static bool Matches(RecapGridCadencePolicySpec value)
        => value.MinimumRecentHistoryLoad
                == Cadence.MinimumRecentHistoryLoad
            && string.Equals(
                value.PartitionAlgorithmId,
                Cadence.PartitionAlgorithmId,
                StringComparison.Ordinal
            )
            && string.Equals(
                value.HistoryLoadEstimatorId,
                Cadence.HistoryLoadEstimatorId,
                StringComparison.Ordinal
            )
            && value.TargetHistoryLoad == Cadence.TargetHistoryLoad
            && value.MaxRawEvents == Cadence.MaxRawEvents
            && value.MaxRenderedBytes == Cadence.MaxRenderedBytes;
}
