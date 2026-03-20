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
        List<ExistingSegment> Segments
    );

    private static class RepositoryOpenValidator {
        public static OpenLayout Validate(string repoFullPath) {
            var branchesDir = GetBranchesDirectoryPath(repoFullPath);
            if (!Directory.Exists(branchesDir)) {
                throw new InvalidDataException($"Repository '{repoFullPath}' is missing required branches directory '{branchesDir}'.");
            }

            var recentDir = Path.Combine(repoFullPath, RecentDirName);
            if (!Directory.Exists(recentDir)) {
                throw new InvalidDataException($"Repository '{repoFullPath}' is missing required segment directory '{recentDir}'.");
            }

            var segments = ScanExistingSegments(repoFullPath, recentDir);
            var knownSegmentNumbers = segments.Select(x => x.SegmentNumber).ToHashSet();
            var branches = LoadBranches(repoFullPath, branchesDir, knownSegmentNumbers);
            return new OpenLayout(branches, segments);
        }

        private static List<LoadedBranch> LoadBranches(
            string repoFullPath,
            string branchesDir,
            IReadOnlySet<uint> knownSegmentNumbers
        ) {
            var branches = new List<LoadedBranch>();

            foreach (var filePath in Directory.GetFiles(branchesDir, "*.json", SearchOption.AllDirectories)) {
                var relPath = Path.GetRelativePath(branchesDir, filePath);
                var branchName = Path.ChangeExtension(relPath, null)!
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                var nameError = ValidateBranchName(branchName);
                if (nameError is not null) {
                    throw new InvalidDataException(
                        $"Branch file '{filePath}' resolved to invalid branch name '{branchName}': {nameError}"
                    );
                }

                var head = ReadBranchAddress(filePath);
                if (head is { } address && !knownSegmentNumbers.Contains(address.SegmentNumber)) {
                    throw new InvalidDataException(
                        $"Branch '{branchName}' points to missing segment {address.SegmentNumber} (from '{filePath}')."
                    );
                }

                branches.Add(new LoadedBranch(branchName, head));
            }

            return branches;
        }

        private static List<ExistingSegment> ScanExistingSegments(string repoFullPath, string recentDir) {
            var segments = new List<ExistingSegment>();

            foreach (var file in Directory.GetFiles(recentDir, "*.sj.rbf")) {
                var fileName = Path.GetFileName(file);
                const string suffix = ".sj.rbf";
                var name = fileName[..^suffix.Length];
                if (!uint.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out var segmentNumber)) {
                    throw new InvalidDataException($"Segment file '{fileName}' does not use the expected hex segment number format.");
                }

                segments.Add(new ExistingSegment(
                    SegmentNumber: segmentNumber,
                    AbsolutePath: file,
                    RelativePath: Path.Combine(RecentDirName, fileName)
                ));
            }

            segments.Sort((a, b) => a.SegmentNumber.CompareTo(b.SegmentNumber));
            if (segments.Count == 0) {
                throw new InvalidDataException($"Repository '{repoFullPath}' does not contain any segment files under '{recentDir}'.");
            }

            for (int i = 0; i < segments.Count; i++) {
                uint expectedSegmentNumber = (uint)(i + 1);
                if (segments[i].SegmentNumber != expectedSegmentNumber) {
                    throw new InvalidDataException(
                        $"Segment numbering is not contiguous in '{recentDir}': expected segment {expectedSegmentNumber} but found {segments[i].SegmentNumber}."
                    );
                }
            }

            return segments;
        }
    }
}
