using System.Collections.Concurrent;
namespace CodeCortex.Workspace.Incremental;
#pragma warning disable 1591
public enum FileChangeKind { Created, Changed, Deleted, Renamed }
public sealed record RawFileChange(FileChangeKind Kind, string Path, string? OldPath = null) {
    public static RawFileChange Created(string p) => new(FileChangeKind.Created, p);
    public static RawFileChange Changed(string p) => new(FileChangeKind.Changed, p);
    public static RawFileChange Deleted(string p) => new(FileChangeKind.Deleted, p);
    public static RawFileChange Renamed(string oldPath, string newPath) => new(FileChangeKind.Renamed, newPath, oldPath);
}
public sealed record ClassifiedFileChange(string Path, ClassifiedKind Kind, string? OldPath = null);
public enum ClassifiedKind { Add, Modify, Delete, Rename }
#pragma warning restore 1591
