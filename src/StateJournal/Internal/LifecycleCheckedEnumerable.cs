using System.Collections;

namespace Atelia.StateJournal.Internal;

/// <summary>
/// 为 owner-backed live enumerable 提供生命周期检查。
/// Snapshot collection 不使用此包装；枚举器 Dispose 始终允许执行清理。
/// </summary>
internal sealed class LifecycleCheckedEnumerable<T> : IEnumerable<T> {
    private readonly DurableObject _owner;
    private readonly IEnumerable<T> _source;

    internal LifecycleCheckedEnumerable(DurableObject owner, IEnumerable<T> source) {
        _owner = owner;
        _source = source;
    }

    public IEnumerator<T> GetEnumerator() {
        _owner.ThrowIfDisposed();
        return new LifecycleCheckedEnumerator(_owner, _source.GetEnumerator());
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class LifecycleCheckedEnumerator : IEnumerator<T> {
        private readonly DurableObject _owner;
        private readonly IEnumerator<T> _inner;

        internal LifecycleCheckedEnumerator(DurableObject owner, IEnumerator<T> inner) {
            _owner = owner;
            _inner = inner;
        }

        public T Current {
            get {
                _owner.ThrowIfDisposed();
                return _inner.Current;
            }
        }

        object? IEnumerator.Current => Current;

        public bool MoveNext() {
            _owner.ThrowIfDisposed();
            return _inner.MoveNext();
        }

        public void Reset() {
            _owner.ThrowIfDisposed();
            _inner.Reset();
        }

        public void Dispose() => _inner.Dispose();
    }
}
