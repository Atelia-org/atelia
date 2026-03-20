using Atelia.Rbf;

namespace Atelia.StateJournal;

public sealed partial class Repository {
    private static FileStream AcquireLock(string repoDir, FileMode mode) {
        var lockPath = Path.Combine(repoDir, LockFileName);
        return new FileStream(lockPath, mode, FileAccess.ReadWrite, FileShare.None);
    }

    private bool HasCommittedBranchPointingIntoActiveSegment() {
        return _maxCommittedSegmentNumber >= _segments.ActiveSegmentNumber;
    }

    private sealed class SegmentCatalog : IDisposable {
        internal readonly record struct PendingRotation(uint SegmentNumber, string RelativePath, IRbfFile File);

        private readonly string _repoDir;
        private readonly Dictionary<string, IRbfFile> _openFiles;
        private readonly List<(uint SegmentNumber, string RelativePath)> _index;
        private string _activeRelativePath;

        private SegmentCatalog(
            string repoDir,
            Dictionary<string, IRbfFile> openFiles,
            List<(uint SegmentNumber, string RelativePath)> index,
            string activeRelativePath
        ) {
            _repoDir = repoDir;
            _openFiles = openFiles;
            _index = index;
            _activeRelativePath = activeRelativePath;
        }

        public IRbfFile ActiveFile => _openFiles[_activeRelativePath];

        public uint ActiveSegmentNumber => _index[^1].SegmentNumber;

        public static SegmentCatalog CreateNew(string repoDir) {
            var recentDir = Path.Combine(repoDir, RecentDirName);
            Directory.CreateDirectory(recentDir);

            const uint firstSegment = 1;
            var relPath = MakeRelativeSegmentPath(firstSegment);
            var fullPath = Path.Combine(repoDir, relPath);
            var activeFile = RbfFile.CreateNew(fullPath);
            var openFiles = new Dictionary<string, IRbfFile>(StringComparer.Ordinal) {
                [relPath] = activeFile,
            };
            var index = new List<(uint, string)> { (firstSegment, relPath) };
            return new SegmentCatalog(repoDir, openFiles, index, relPath);
        }

        public static SegmentCatalog OpenFromScan(
            string repoDir,
            IReadOnlyList<ExistingSegment> scannedSegments
        ) {
            var openFiles = new Dictionary<string, IRbfFile>(StringComparer.Ordinal);
            var index = new List<(uint, string)>(scannedSegments.Count);
            foreach (var segment in scannedSegments) {
                index.Add((segment.SegmentNumber, segment.RelativePath));
            }

            var activeRelPath = index[^1].Item2;
            openFiles[activeRelPath] = RbfFile.OpenExisting(scannedSegments[^1].AbsolutePath);
            return new SegmentCatalog(repoDir, openFiles, index, activeRelPath);
        }

        public IRbfFile GetFileForSegment(uint segmentNumber) {
            var idx = (int)(segmentNumber - 1); // 1-based → 0-based
            if (idx < 0 || idx >= _index.Count) {
                throw new InvalidOperationException(
                    $"No segment found for segment number {segmentNumber}. Known segments: 1–{_index.Count}."
                );
            }

            var relativePath = _index[idx].RelativePath;
            if (_openFiles.TryGetValue(relativePath, out var existing)) { return existing; }

            var fullPath = Path.Combine(_repoDir, relativePath);
            var file = RbfFile.OpenExisting(fullPath);
            _openFiles[relativePath] = file;
            return file;
        }

        public bool ShouldRotate(long threshold) {
            return ActiveFile.TailOffset > threshold;
        }

        public PendingRotation OpenPendingRotation() {
            var nextSegNum = (uint)(_index.Count + 1);
            var relativePath = MakeRelativeSegmentPath(nextSegNum);
            var fullPath = Path.Combine(_repoDir, relativePath);
            var file = RbfFile.CreateNew(fullPath);
            _openFiles[relativePath] = file;
            return new PendingRotation(nextSegNum, relativePath, file);
        }

        public void CommitRotation(PendingRotation rotation) {
            _activeRelativePath = rotation.RelativePath;
            _index.Add((rotation.SegmentNumber, rotation.RelativePath));
        }

        public void RollbackRotation(PendingRotation rotation) {
            _openFiles.Remove(rotation.RelativePath);
            rotation.File.Dispose();
            TryDeleteSegmentFile(_repoDir, rotation.RelativePath);
        }

        public void Dispose() {
            foreach (var file in _openFiles.Values) {
                file.Dispose();
            }
        }

        private static string MakeRelativeSegmentPath(uint segmentNumber) {
            return Path.Combine(RecentDirName, $"{segmentNumber:X8}.sj.rbf");
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
