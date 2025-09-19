namespace CodeCortex.Workspace.Incremental;
#pragma warning disable 1591
public interface IChangeClassifier { IReadOnlyList<ClassifiedFileChange> Classify(IReadOnlyList<RawFileChange> raw); }
public sealed class ChangeClassifier : IChangeClassifier {
    public IReadOnlyList<ClassifiedFileChange> Classify(IReadOnlyList<RawFileChange> raw) {
        // Simple fold: keep last state per path; handle rename explicitly.
        var map = new Dictionary<string, ClassifiedFileChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in raw) {
            switch (r.Kind) {
                case FileChangeKind.Created:
                    map[r.Path] = new ClassifiedFileChange(r.Path, ClassifiedKind.Add);
                    break;
                case FileChangeKind.Changed:
                    if (!map.TryGetValue(r.Path, out var existing)) {
                        map[r.Path] = new ClassifiedFileChange(r.Path, ClassifiedKind.Modify);
                    }
                    else if (existing.Kind is ClassifiedKind.Add or ClassifiedKind.Modify) {
                        // stay Add/Modify
                    }
                    else if (existing.Kind == ClassifiedKind.Delete) {
                        // delete then change -> treat final as modify
                        map[r.Path] = new ClassifiedFileChange(r.Path, ClassifiedKind.Modify);
                    }
                    break;
                case FileChangeKind.Deleted:
                    map[r.Path] = new ClassifiedFileChange(r.Path, ClassifiedKind.Delete);
                    break;
                case FileChangeKind.Renamed:
                    if (!string.IsNullOrEmpty(r.OldPath)) {
                        map[r.OldPath] = new ClassifiedFileChange(r.OldPath, ClassifiedKind.Rename, r.Path);
                        map[r.Path] = new ClassifiedFileChange(r.Path, ClassifiedKind.Add, r.OldPath); // treat new path as add
                    }
                    else {
                        map[r.Path] = new ClassifiedFileChange(r.Path, ClassifiedKind.Modify);
                    }
                    break;
            }
        }
        return map.Values.ToList();
    }
}
#pragma warning restore 1591
