namespace Atelia.StateJournal;

public sealed partial class Repository {
    private readonly record struct LoadedBranch(
        string BranchName,
        CommitAddress? Head
    );

    private readonly record struct ExistingSegment(
        uint SegmentNumber,
        string AbsolutePath,
        string RelativePath
    );

    private sealed record OpenLayout(
        List<LoadedBranch> Branches,
        List<ExistingSegment> RecentSegments
    );

    private static class RepositoryOpenValidator {
        public static OpenLayout Validate(string repoFullPath) {
            var branchesFullDir = GetBranchesDirectoryPath(repoFullPath);
            if (!Directory.Exists(branchesFullDir)) { throw new InvalidDataException($"Repository '{repoFullPath}' is missing required branches directory '{branchesFullDir}'."); }

            var recentDir = Path.Combine(repoFullPath, RecentDirName);
            if (!Directory.Exists(recentDir)) { throw new InvalidDataException($"Repository '{repoFullPath}' is missing required segment directory '{recentDir}'."); }

            var recentSegments = ScanRecentSegments(recentDir);
            if (recentSegments.Count == 0) { throw new InvalidDataException($"Repository '{repoFullPath}' does not contain any segment files under '{recentDir}'."); }

            var archiveDir = Path.Combine(repoFullPath, ArchiveDirName);
            var archivedSegments = ScanArchivedSegments(repoFullPath, archiveDir);
            var maxSegmentNumber = ValidateSegmentLayout(repoFullPath, recentSegments, archivedSegments);
            var branches = LoadBranches(repoFullPath, branchesFullDir, maxSegmentNumber);
            return new OpenLayout(branches, recentSegments);
        }

        private static List<LoadedBranch> LoadBranches(
            string repoFullPath,
            string branchesFullDir,
            uint maxSegmentNumber
        ) {
            var branches = new List<LoadedBranch>();
            var branchNames = DiscoverBranchNames(branchesFullDir);

            foreach (var branchName in branchNames) {
                var nameError = ValidateBranchName(branchName);
                if (nameError is not null) {
                    throw new InvalidDataException(
                        $"Branch metadata resolved to invalid branch name '{branchName}': {nameError}"
                    );
                }

                var branchRef = ReadBestBranchRefOrDefault(repoFullPath, branchName)
                    ?? throw new InvalidDataException(
                        $"Branch '{branchName}' has no readable metadata in primary ref, backup ref, or reflog."
                    );
                var head = branchRef.Head;
                if (head is { } address && (address.SegmentNumber == 0 || address.SegmentNumber > maxSegmentNumber)) {
                    throw new InvalidDataException(
                        $"Branch '{branchName}' points to missing segment {address.SegmentNumber} (from '{branchRef.SourcePath}')."
                    );
                }

                branches.Add(new LoadedBranch(branchName, head));
            }

            return branches;
        }

        private static SortedSet<string> DiscoverBranchNames(string branchesFullDir) {
            var branchNames = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var filePath in Directory.EnumerateFiles(branchesFullDir, "*", SearchOption.AllDirectories)) {
                var relPath = Path.GetRelativePath(branchesFullDir, filePath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');

                var branchName = TryResolveBranchName(relPath);
                if (branchName is not null) {
                    branchNames.Add(branchName);
                }
            }

            return branchNames;
        }

        private static string? TryResolveBranchName(string relativePath) {
            if (relativePath.EndsWith(".json.last", StringComparison.Ordinal)) { return relativePath[..^".json.last".Length]; }

            if (relativePath.EndsWith(".reflog.jsonl", StringComparison.Ordinal)) { return relativePath[..^".reflog.jsonl".Length]; }

            if (relativePath.EndsWith(".json", StringComparison.Ordinal)) { return relativePath[..^".json".Length]; }

            return null;
        }

        private static List<ExistingSegment> ScanRecentSegments(string recentDir) {
            var segments = new List<ExistingSegment>();

            foreach (var file in Directory.GetFiles(recentDir, "*.sj.rbf")) {
                var fileName = Path.GetFileName(file);
                segments.Add(
                    new ExistingSegment(
                        SegmentNumber: ParseSegmentNumber(fileName),
                        AbsolutePath: file,
                        RelativePath: Path.Combine(RecentDirName, fileName)
                    )
                );
            }

            segments.Sort((a, b) => a.SegmentNumber.CompareTo(b.SegmentNumber));
            return segments;
        }

        private static List<ExistingSegment> ScanArchivedSegments(string repoFullPath, string archiveDir) {
            var segments = new List<ExistingSegment>();
            if (!Directory.Exists(archiveDir)) { return segments; }

            foreach (var file in Directory.GetFiles(archiveDir, "*.sj.rbf", SearchOption.AllDirectories)) {
                var fileName = Path.GetFileName(file);
                var segmentNumber = ParseSegmentNumber(fileName);
                var relativePath = Path.GetRelativePath(repoFullPath, file);
                var expectedRelativePath = MakeArchiveRelativeSegmentPath(segmentNumber);
                if (!string.Equals(relativePath, expectedRelativePath, StringComparison.Ordinal)) {
                    throw new InvalidDataException(
                        $"Archived segment {segmentNumber} is stored at '{relativePath}', expected '{expectedRelativePath}'."
                    );
                }

                segments.Add(
                    new ExistingSegment(
                        SegmentNumber: segmentNumber,
                        AbsolutePath: file,
                        RelativePath: relativePath
                    )
                );
            }

            segments.Sort((a, b) => a.SegmentNumber.CompareTo(b.SegmentNumber));
            return segments;
        }

        private static uint ValidateSegmentLayout(
            string repoFullPath,
            List<ExistingSegment> recentSegments,
            List<ExistingSegment> archivedSegments
        ) {
            // 验证 archive 部分内部连续性: 1, 2, ..., archivedSegments.Count
            for (int i = 0; i < archivedSegments.Count; i++) {
                uint expectedSegmentNumber = (uint)(i + 1);
                if (archivedSegments[i].SegmentNumber != expectedSegmentNumber) {
                    throw new InvalidDataException(
                        $"Segment numbering is not contiguous in repository '{repoFullPath}': expected segment {expectedSegmentNumber} but found {archivedSegments[i].SegmentNumber}."
                    );
                }
            }

            // 验证 recent 部分衔接 archive 并内部连续，天然保证 recent 是最新连续后缀
            for (int i = 0; i < recentSegments.Count; i++) {
                uint expectedSegmentNumber = (uint)(archivedSegments.Count + i + 1);
                if (recentSegments[i].SegmentNumber != expectedSegmentNumber) {
                    throw new InvalidDataException(
                        $"Segment numbering is not contiguous in repository '{repoFullPath}': expected segment {expectedSegmentNumber} but found {recentSegments[i].SegmentNumber}."
                    );
                }
            }

            return (uint)(archivedSegments.Count + recentSegments.Count);
        }

        private static uint ParseSegmentNumber(string fileName) {
            const string suffix = ".sj.rbf";
            var name = fileName[..^suffix.Length];
            if (!uint.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out var segmentNumber)) { throw new InvalidDataException($"Segment file '{fileName}' does not use the expected hex segment number format."); }

            return segmentNumber;
        }
    }
}
