using CodeCortex.Core.Index;
namespace CodeCortex.Workspace.Incremental;
#pragma warning disable 1591
public sealed class ReverseIndexCache {
    private readonly Dictionary<string, List<string>> _fileToTypes = new(StringComparer.OrdinalIgnoreCase);
    public ReverseIndexCache(CodeCortexIndex index) { BuildFrom(index); }
    public void BuildFrom(CodeCortexIndex index) {
        _fileToTypes.Clear();
        foreach (var t in index.Types) {
            foreach (var f in t.Files) {
                if (!_fileToTypes.TryGetValue(f, out var list)) {
                    list = new();
                    _fileToTypes[f] = list;
                }
                if (!list.Contains(t.Id)) {
                    list.Add(t.Id);
                }
            }
        }
    }
    public IReadOnlyList<string> GetIdsByFile(string path) => _fileToTypes.TryGetValue(path, out var list) ? list : Array.Empty<string>();
    public void ApplyTypeUpdate(string typeId, IReadOnlyList<string> oldFiles, IReadOnlyList<string> newFiles) {
        var oldSet = new HashSet<string>(oldFiles, StringComparer.OrdinalIgnoreCase);
        var newSet = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var f in oldSet.Except(newSet)) {
            if (_fileToTypes.TryGetValue(f, out var list)) {
                list.RemoveAll(id => id == typeId);
                if (list.Count == 0) {
                    _fileToTypes.Remove(f);
                }
            }
        }
        foreach (var f in newSet) {
            if (!_fileToTypes.TryGetValue(f, out var list)) {
                list = new();
                _fileToTypes[f] = list;
            }
            if (!list.Contains(typeId)) {
                list.Add(typeId);
            }
        }
    }
    public void RemoveType(string typeId) {
        var toRemove = new List<string>();
        foreach (var kv in _fileToTypes) {
            kv.Value.RemoveAll(id => id == typeId);
            if (kv.Value.Count == 0) {
                toRemove.Add(kv.Key);
            }
        }
        foreach (var f in toRemove) {
            _fileToTypes.Remove(f);
        }
    }
    public void RemoveFile(string path) { _fileToTypes.Remove(path); }
}
#pragma warning restore 1591
