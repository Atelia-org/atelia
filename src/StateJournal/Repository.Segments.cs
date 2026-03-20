using Atelia.Rbf;

namespace Atelia.StateJournal;

public sealed partial class Repository {
    private static FileStream AcquireLock(string repoDir, FileMode mode) {
        var lockPath = Path.Combine(repoDir, LockFileName);
        return new FileStream(lockPath, mode, FileAccess.ReadWrite, FileShare.None);
    }

    private static string MakeRecentRelativeSegmentPath(uint segmentNumber) {
        return Path.Combine(RecentDirName, MakeSegmentFileName(segmentNumber));
    }

    private static string MakeArchiveRelativeSegmentPath(uint segmentNumber) {
        ArgumentOutOfRangeException.ThrowIfZero(segmentNumber);

        var bucketStart = ((segmentNumber - 1) / ArchivedSegmentBucketSize) * (uint)ArchivedSegmentBucketSize + 1;
        var bucketEnd = bucketStart + (uint)ArchivedSegmentBucketSize - 1;
        var bucketDir = $"{bucketStart:X8}-{bucketEnd:X8}";
        return Path.Combine(ArchiveDirName, bucketDir, MakeSegmentFileName(segmentNumber));
    }

    private static string MakeSegmentFileName(uint segmentNumber) {
        return $"{segmentNumber:X8}.sj.rbf";
    }

    private bool HasCommittedBranchPointingIntoActiveSegment() {
        return _maxCommittedSegmentNumber >= _segments.ActiveSegmentNumber;
    }

    private sealed class SegmentCatalog : IDisposable {
        internal readonly record struct PendingRotation(uint SegmentNumber, string RelativePath, IRbfFile File);

        private readonly string _repoDir;
        private uint _recentBaseSegmentNumber;
        private int _recentCount;
        private IRbfFile _activeFile;

        private SegmentCatalog(
            string repoDir,
            uint recentBaseSegmentNumber,
            int recentCount,
            IRbfFile activeFile
        ) {
            _repoDir = repoDir;
            _recentBaseSegmentNumber = recentBaseSegmentNumber;
            _recentCount = recentCount;
            _activeFile = activeFile;
        }

        public IRbfFile ActiveFile => _activeFile;

        public uint ActiveSegmentNumber => _recentBaseSegmentNumber + (uint)_recentCount - 1;

        public static SegmentCatalog CreateNew(string repoDir) {
            var recentDir = Path.Combine(repoDir, RecentDirName);
            Directory.CreateDirectory(recentDir);

            const uint firstSegment = 1;
            var relPath = MakeRecentRelativeSegmentPath(firstSegment);
            var fullPath = Path.Combine(repoDir, relPath);
            var activeFile = RbfFile.CreateNew(fullPath);
            return new SegmentCatalog(repoDir, firstSegment, 1, activeFile);
        }

        public static SegmentCatalog OpenFromScan(
            string repoDir,
            IReadOnlyList<ExistingSegment> recentSegments
        ) {
            var activeFile = RbfFile.OpenExisting(recentSegments[^1].AbsolutePath);
            return new SegmentCatalog(
                repoDir,
                recentSegments[0].SegmentNumber,
                recentSegments.Count,
                activeFile
            );
        }

        public IRbfFile OpenHistoricalFile(uint segmentNumber) {
            if (segmentNumber == 0 || segmentNumber > ActiveSegmentNumber) {
                throw new InvalidOperationException(
                    $"No segment found for segment number {segmentNumber}. Known segments: 1–{ActiveSegmentNumber}."
                );
            }

            if (segmentNumber == ActiveSegmentNumber) {
                throw new InvalidOperationException(
                    $"Segment {segmentNumber} is the active segment and should use the active file instance."
                );
            }

            string relativePath;
            if (segmentNumber < _recentBaseSegmentNumber) {
                relativePath = MakeArchiveRelativeSegmentPath(segmentNumber);
            }
            else {
                relativePath = MakeRecentRelativeSegmentPath(segmentNumber);
            }

            var fullPath = Path.Combine(_repoDir, relativePath);
            return RbfFile.OpenReadOnlyExisting(fullPath);
        }

        public bool ShouldRotate(long threshold) {
            return ActiveFile.TailOffset > threshold;
        }

        public PendingRotation OpenPendingRotation() {
            var nextSegNum = ActiveSegmentNumber + 1;
            var relativePath = MakeRecentRelativeSegmentPath(nextSegNum);
            var fullPath = Path.Combine(_repoDir, relativePath);
            var file = RbfFile.CreateNew(fullPath);
            return new PendingRotation(nextSegNum, relativePath, file);
        }

        public void CommitRotation(PendingRotation rotation) {
            _activeFile.Dispose();
            _activeFile = rotation.File;
            _recentCount++;
        }

        public void ArchiveExcessRecentSegments() {
            while (_recentCount > RecentSegmentWindowTargetCount) {
                var segmentNumber = _recentBaseSegmentNumber;
                var sourceRelativePath = MakeRecentRelativeSegmentPath(segmentNumber);
                var targetRelativePath = MakeArchiveRelativeSegmentPath(segmentNumber);
                var sourceFullPath = Path.Combine(_repoDir, sourceRelativePath);
                var targetFullPath = Path.Combine(_repoDir, targetRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFullPath)!);
                File.Move(sourceFullPath, targetFullPath);

                _recentCount--;
                _recentBaseSegmentNumber++;
            }
        }

        public void RollbackRotation(PendingRotation rotation) {
            rotation.File.Dispose();
            TryDeleteSegmentFile(_repoDir, rotation.RelativePath);
        }

        public void Dispose() {
            _activeFile.Dispose();
        }

        private static void TryDeleteSegmentFile(string repoDir, string relativePath) {
            try {
                File.Delete(Path.Combine(repoDir, relativePath));
            }
            catch {
                // best-effort cleanup
            }
        }
    }
}
