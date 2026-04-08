namespace Atelia.StateJournal.Tests;

internal static class SegmentPathTestHelper {
    public static string SegmentFileName(uint segmentNumber) {
        return $"{segmentNumber:X8}.sj.rbf";
    }

    public static string RecentSegmentPath(string repoDir, uint segmentNumber) {
        return Path.Combine(repoDir, "recent", SegmentFileName(segmentNumber));
    }

    public static string ArchiveBucketDirectoryName(uint segmentNumber) {
        var bucketStart = ((segmentNumber - 1) / (uint)Repository.ArchivedSegmentBucketSize)
            * (uint)Repository.ArchivedSegmentBucketSize + 1;
        var bucketEnd = bucketStart + (uint)Repository.ArchivedSegmentBucketSize - 1;
        return $"{bucketStart:X8}-{bucketEnd:X8}";
    }

    public static string ArchiveSegmentPath(string repoDir, uint segmentNumber) {
        return Path.Combine(repoDir, "archive", ArchiveBucketDirectoryName(segmentNumber), SegmentFileName(segmentNumber));
    }
}
