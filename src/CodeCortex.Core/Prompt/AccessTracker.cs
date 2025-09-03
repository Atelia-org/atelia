using System;
using System.Collections.Generic;

namespace CodeCortex.Core.Prompt {
    /// <summary>
    /// 维护最近访问的类型（LRU），用于 Prompt 窗口 Focus/Recent 区域。
    /// </summary>
    public class AccessTracker<T> {
        private readonly int _capacity;
        private readonly LinkedList<T> _list = new();
        private readonly HashSet<T> _set = new();
        private readonly Dictionary<T, LinkedListNode<T>> _map = new();

        public AccessTracker(int capacity = 32) {
            _capacity = capacity;
        }

        public void Access(T item) {
            if (_set.Contains(item)) {
                var node = _map[item];
                _list.Remove(node);
                _list.AddFirst(node);
            } else {
                if (_list.Count >= _capacity) {
                    var last = _list.Last;
                    if (last != null) {
                        _set.Remove(last.Value);
                        _map.Remove(last.Value);
                        _list.RemoveLast();
                    }
                }
                var newNode = new LinkedListNode<T>(item);
                _list.AddFirst(newNode);
                _set.Add(item);
                _map[item] = newNode;
            }
        }

        public IReadOnlyList<T> GetRecent(int count) {
            var result = new List<T>(count);
            var node = _list.First;
            int i = 0;
            while (node != null && i < count) {
                result.Add(node.Value);
                node = node.Next;
                i++;
            }
            return result;
        }

        public IReadOnlyList<T> GetAll() => new List<T>(_list);
    }
}
