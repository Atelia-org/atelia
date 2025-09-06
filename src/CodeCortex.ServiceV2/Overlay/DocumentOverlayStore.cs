using System.Collections.Concurrent;

namespace CodeCortex.ServiceV2.Overlay;

public sealed class DocumentOverlayStore {
    private readonly ConcurrentDictionary<string, string> _textByDocPath = new();

    public void SetText(string filePath, string text) => _textByDocPath[filePath] = text;
    public bool TryGetText(string filePath, out string text) => _textByDocPath.TryGetValue(filePath, out text!);
    public void Remove(string filePath) => _textByDocPath.TryRemove(filePath, out _);
    public void Clear() => _textByDocPath.Clear();
}

