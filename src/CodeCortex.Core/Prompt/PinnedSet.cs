using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CodeCortex.Core.Prompt {
    /// <summary>
    /// 管理已 Pin 的类型集合，支持内存操作与简单 JSON 持久化。
    /// </summary>
    public class PinnedSet<T> {
        private HashSet<T> _set = new();
        private readonly string? _persistPath;
        private readonly CodeCortex.Core.IO.IFileSystem _fs;


        public PinnedSet(string? persistPath = null, CodeCortex.Core.IO.IFileSystem? fs = null, IEnumerable<T>? initial = null) {
            _persistPath = persistPath;
            _fs = fs ?? new CodeCortex.Core.IO.DefaultFileSystem();
            if (initial != null) {
                _set = new HashSet<T>(initial);
            }
            else if (!string.IsNullOrEmpty(_persistPath) && _fs.FileExists(_persistPath)) {
                try {
                    var json = _fs.ReadAllText(_persistPath);
                    var items = JsonSerializer.Deserialize<HashSet<T>>(json);
                    if (items != null) {
                        _set = items;
                    }
                }
                catch { /* ignore */ }
            }
        }

        public bool Add(T item) {
            var added = _set.Add(item);
            if (added) {
                Persist();
            }

            return added;
        }

        public bool Remove(T item) {
            var removed = _set.Remove(item);
            if (removed) {
                Persist();
            }

            return removed;
        }

        public bool Contains(T item) => _set.Contains(item);
        public IReadOnlyCollection<T> Items => _set;

        private void Persist() {
            if (!string.IsNullOrEmpty(_persistPath)) {
                try {
                    _fs.WriteAllText(_persistPath, JsonSerializer.Serialize(_set));
                }
                catch { /* ignore */ }
            }
        }
    }
}
