namespace Atelia.StateJournal.Internal.Tests;

/// <summary>
/// 链表版 <see cref="BoolDivision{TKey}"/> 合约测试。
/// 测试用例由 <see cref="BoolDivisionContractTests"/> 基类提供。
/// </summary>
public class BoolDivisionTests : BoolDivisionContractTests {
    protected override ITestBoolDivision<string> CreateString(IEqualityComparer<string>? comparer = null)
        => new Adapter<string>(new BoolDivision<string>(comparer));

    protected override ITestBoolDivision<int> CreateInt(IEqualityComparer<int>? comparer = null)
        => new Adapter<int>(new BoolDivision<int>(comparer));

    private class Adapter<TKey>(BoolDivision<TKey> inner) : ITestBoolDivision<TKey> where TKey : notnull {
        public int Count => inner.Count;
        public int FalseCount => inner.FalseCount;
        public int TrueCount => inner.TrueCount;
        public int Capacity => inner.Capacity;
        public void SetFalse(TKey key) => inner.SetFalse(key);
        public void SetTrue(TKey key) => inner.SetTrue(key);
        public void Remove(TKey key) => inner.Remove(key);
        public void Clear() => inner.Clear();

        public List<TKey> CollectFalseKeys() {
            var list = new List<TKey>();
            foreach (var k in inner.FalseKeys) { list.Add(k); }
            return list;
        }

        public List<TKey> CollectTrueKeys() {
            var list = new List<TKey>();
            foreach (var k in inner.TrueKeys) { list.Add(k); }
            return list;
        }
    }
}
