using System.Collections;

namespace Atelia.StateJournal3;

partial class DurableDict<TKey, TValue> {
    public ICollection<TValue> Values {
        get {
            throw new NotImplementedException();
        }
    }

    public bool IsReadOnly {
        get {
            throw new NotImplementedException();
        }
    }

    public void Add(TKey key, TValue value) {
        throw new NotImplementedException();
    }

    public void Add(KeyValuePair<TKey, TValue> item) {
        throw new NotImplementedException();
    }

    public void Clear() {
        throw new NotImplementedException();
    }

    public bool Contains(KeyValuePair<TKey, TValue> item) {
        throw new NotImplementedException();
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) {
        throw new NotImplementedException();
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() {
        throw new NotImplementedException();
    }

    public bool Remove(KeyValuePair<TKey, TValue> item) {
        throw new NotImplementedException();
    }

    IEnumerator IEnumerable.GetEnumerator() {
        return GetEnumerator();
    }
}
