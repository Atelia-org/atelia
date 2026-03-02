using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

/// <summary>
/// 测试适配器接口，抹平 <c>BoolDivision</c> 与 <c>BitmapBoolDivision</c> 的
/// <c>ref struct Enumerator</c> 差异，使合约测试跨实现复用。
/// 仅用于测试项目。
/// </summary>
public interface ITestBoolDivision<TKey> where TKey : notnull {
    int Count { get; }
    int FalseCount { get; }
    int TrueCount { get; }
    int Capacity { get; }
    void SetFalse(TKey key);
    void SetTrue(TKey key);
    void Remove(TKey key);
    void Clear();
    List<TKey> CollectFalseKeys();
    List<TKey> CollectTrueKeys();
}

/// <summary>
/// BoolDivision 合约测试基类。
/// 子类通过 <see cref="CreateString"/> / <see cref="CreateInt"/> 提供具体实现。
/// xUnit 会从子类自动发现所有 [Fact] 测试。
/// </summary>
public abstract class BoolDivisionContractTests {
    protected abstract ITestBoolDivision<string> CreateString(IEqualityComparer<string>? comparer = null);
    protected abstract ITestBoolDivision<int> CreateInt(IEqualityComparer<int>? comparer = null);

    // ────────── 构造 & 初始状态 ──────────

    [Fact]
    public void NewInstance_IsEmpty() {
        var bd = CreateString();
        Assert.Equal(0, bd.Count);
        Assert.Equal(0, bd.FalseCount);
        Assert.Equal(0, bd.TrueCount);
        Assert.True(bd.Capacity >= 4);
    }

    // ────────── SetFalse / SetTrue 基础 ──────────

    [Fact]
    public void SetFalse_NewKey_AddsToFalseSubset() {
        var bd = CreateString();
        bd.SetFalse("a");
        Assert.Equal(1, bd.Count);
        Assert.Equal(1, bd.FalseCount);
        Assert.Equal(0, bd.TrueCount);
        Assert.Equal(["a"], bd.CollectFalseKeys());
        Assert.Empty(bd.CollectTrueKeys());
    }

    [Fact]
    public void SetTrue_NewKey_AddsToTrueSubset() {
        var bd = CreateString();
        bd.SetTrue("a");
        Assert.Equal(1, bd.Count);
        Assert.Equal(0, bd.FalseCount);
        Assert.Equal(1, bd.TrueCount);
        Assert.Empty(bd.CollectFalseKeys());
        Assert.Equal(["a"], bd.CollectTrueKeys());
    }

    // ────────── 幂等 ──────────

    [Fact]
    public void SetFalse_SameKey_Twice_IsIdempotent() {
        var bd = CreateString();
        bd.SetFalse("a");
        bd.SetFalse("a");
        Assert.Equal(1, bd.Count);
        Assert.Equal(1, bd.FalseCount);
        Assert.Equal(0, bd.TrueCount);
    }

    [Fact]
    public void SetTrue_SameKey_Twice_IsIdempotent() {
        var bd = CreateString();
        bd.SetTrue("a");
        bd.SetTrue("a");
        Assert.Equal(1, bd.Count);
        Assert.Equal(0, bd.FalseCount);
        Assert.Equal(1, bd.TrueCount);
    }

    // ────────── 子集间移动 ──────────

    [Fact]
    public void SetTrue_AfterSetFalse_MovesToTrueSubset() {
        var bd = CreateString();
        bd.SetFalse("a");
        bd.SetTrue("a");
        Assert.Equal(1, bd.Count);
        Assert.Equal(0, bd.FalseCount);
        Assert.Equal(1, bd.TrueCount);
        Assert.Empty(bd.CollectFalseKeys());
        Assert.Equal(["a"], bd.CollectTrueKeys());
    }

    [Fact]
    public void SetFalse_AfterSetTrue_MovesToFalseSubset() {
        var bd = CreateString();
        bd.SetTrue("a");
        bd.SetFalse("a");
        Assert.Equal(1, bd.Count);
        Assert.Equal(1, bd.FalseCount);
        Assert.Equal(0, bd.TrueCount);
        Assert.Equal(["a"], bd.CollectFalseKeys());
        Assert.Empty(bd.CollectTrueKeys());
    }

    [Fact]
    public void MoveKey_BetweenSubsets_MultipleKeys() {
        var bd = CreateString();
        bd.SetFalse("a");
        bd.SetFalse("b");
        bd.SetTrue("c");

        Assert.Equal(3, bd.Count);
        Assert.Equal(2, bd.FalseCount);
        Assert.Equal(1, bd.TrueCount);

        // 将 b 从 false 移到 true
        bd.SetTrue("b");
        Assert.Equal(3, bd.Count);
        Assert.Equal(1, bd.FalseCount);
        Assert.Equal(2, bd.TrueCount);
        Assert.Equal(["a"], bd.CollectFalseKeys());
        Assert.Contains("b", bd.CollectTrueKeys());
        Assert.Contains("c", bd.CollectTrueKeys());
    }

    // ────────── Remove ──────────

    [Fact]
    public void Remove_ExistingFalseKey_RemovesIt() {
        var bd = CreateString();
        bd.SetFalse("a");
        bd.Remove("a");
        Assert.Equal(0, bd.Count);
        Assert.Equal(0, bd.FalseCount);
        Assert.Equal(0, bd.TrueCount);
        Assert.Empty(bd.CollectFalseKeys());
    }

    [Fact]
    public void Remove_ExistingTrueKey_RemovesIt() {
        var bd = CreateString();
        bd.SetTrue("a");
        bd.Remove("a");
        Assert.Equal(0, bd.Count);
        Assert.Equal(0, bd.FalseCount);
        Assert.Equal(0, bd.TrueCount);
        Assert.Empty(bd.CollectTrueKeys());
    }

    [Fact]
    public void Remove_NonExistentKey_IsNoOp() {
        var bd = CreateString();
        bd.SetFalse("a");
        bd.Remove("x");
        Assert.Equal(1, bd.Count);
        Assert.Equal(1, bd.FalseCount);
    }

    [Fact]
    public void Remove_MiddleElement_PreservesOthers() {
        var bd = CreateString();
        bd.SetFalse("a");
        bd.SetFalse("b");
        bd.SetFalse("c");

        bd.Remove("b");
        Assert.Equal(2, bd.Count);
        Assert.Equal(2, bd.FalseCount);
        var falseKeys = bd.CollectFalseKeys();
        Assert.Contains("a", falseKeys);
        Assert.Contains("c", falseKeys);
        Assert.DoesNotContain("b", falseKeys);
    }

    // ────────── Clear ──────────

    [Fact]
    public void Clear_ResetsEverything() {
        var bd = CreateString();
        bd.SetFalse("a");
        bd.SetTrue("b");
        bd.SetFalse("c");

        bd.Clear();
        Assert.Equal(0, bd.Count);
        Assert.Equal(0, bd.FalseCount);
        Assert.Equal(0, bd.TrueCount);
        Assert.Empty(bd.CollectFalseKeys());
        Assert.Empty(bd.CollectTrueKeys());
    }

    [Fact]
    public void Clear_EmptyCollection_IsNoOp() {
        var bd = CreateString();
        bd.Clear();
        Assert.Equal(0, bd.Count);
    }

    [Fact]
    public void Clear_ThenReuse_WorksCorrectly() {
        var bd = CreateString();
        bd.SetFalse("a");
        bd.SetTrue("b");
        bd.Clear();

        bd.SetTrue("x");
        bd.SetFalse("y");
        Assert.Equal(2, bd.Count);
        Assert.Equal(1, bd.FalseCount);
        Assert.Equal(1, bd.TrueCount);
        Assert.Equal(["y"], bd.CollectFalseKeys());
        Assert.Equal(["x"], bd.CollectTrueKeys());
    }

    // ────────── 扩容 ──────────

    [Fact]
    public void Resize_TriggeredByManyKeys_PreservesAllData() {
        var bd = CreateInt();
        int n = 100;
        for (int i = 0; i < n; i++) {
            if (i % 2 == 0) { bd.SetFalse(i); }
            else { bd.SetTrue(i); }
        }

        Assert.Equal(n, bd.Count);
        Assert.Equal(50, bd.FalseCount);
        Assert.Equal(50, bd.TrueCount);

        var falseKeys = bd.CollectFalseKeys();
        var trueKeys = bd.CollectTrueKeys();
        Assert.Equal(50, falseKeys.Count);
        Assert.Equal(50, trueKeys.Count);

        for (int i = 0; i < n; i++) {
            if (i % 2 == 0) { Assert.Contains(i, falseKeys); }
            else { Assert.Contains(i, trueKeys); }
        }
    }

    // ────────── Slot 复用 ──────────

    [Fact]
    public void SlotReuse_AfterRemove_DoesNotLeak() {
        var bd = CreateInt();
        for (int i = 0; i < 4; i++) { bd.SetFalse(i); }
        Assert.Equal(4, bd.Count);

        for (int i = 0; i < 4; i++) { bd.Remove(i); }
        Assert.Equal(0, bd.Count);

        int capacityBefore = bd.Capacity;
        for (int i = 10; i < 14; i++) { bd.SetTrue(i); }
        Assert.Equal(4, bd.Count);
        Assert.Equal(capacityBefore, bd.Capacity);
    }

    // ────────── 来回移动 ──────────

    [Fact]
    public void Ping_Pong_BetweenSubsets() {
        var bd = CreateString();
        bd.SetFalse("key");
        for (int i = 0; i < 10; i++) {
            bd.SetTrue("key");
            Assert.Equal(0, bd.FalseCount);
            Assert.Equal(1, bd.TrueCount);
            bd.SetFalse("key");
            Assert.Equal(1, bd.FalseCount);
            Assert.Equal(0, bd.TrueCount);
        }
        Assert.Equal(1, bd.Count);
    }

    // ────────── Enumerator ──────────

    [Fact]
    public void Enumerator_EmptySubset_YieldsNothing() {
        var bd = CreateString();
        Assert.Empty(bd.CollectFalseKeys());
        Assert.Empty(bd.CollectTrueKeys());
    }

    [Fact]
    public void Enumerator_SingleSubset_EnumeratesAll() {
        var bd = CreateInt();
        bd.SetTrue(1);
        bd.SetTrue(2);
        bd.SetTrue(3);
        var items = bd.CollectTrueKeys();
        Assert.Equal(3, items.Count);
        Assert.Contains(1, items);
        Assert.Contains(2, items);
        Assert.Contains(3, items);
    }

    // ────────── Count 一致性不变量 ──────────

    [Fact]
    public void Count_AlwaysEquals_FalseCount_Plus_TrueCount() {
        var bd = CreateInt();
        AssertCountInvariant(bd);

        bd.SetFalse(1);
        AssertCountInvariant(bd);
        bd.SetTrue(2);
        AssertCountInvariant(bd);
        bd.SetTrue(1); // move
        AssertCountInvariant(bd);
        bd.Remove(2);
        AssertCountInvariant(bd);
        bd.SetFalse(3);
        AssertCountInvariant(bd);
        bd.Clear();
        AssertCountInvariant(bd);
    }

    // ────────── 自定义 Comparer ──────────

    [Fact]
    public void CustomComparer_IsRespected() {
        var bd = CreateString(StringComparer.OrdinalIgnoreCase);
        bd.SetFalse("Hello");
        bd.SetTrue("HELLO"); // 应视为同一个 key，移到 true
        Assert.Equal(1, bd.Count);
        Assert.Equal(0, bd.FalseCount);
        Assert.Equal(1, bd.TrueCount);
    }

    // ────────── 值类型 key ──────────

    [Fact]
    public void ValueTypeKey_WorksCorrectly() {
        var bd = CreateInt();
        bd.SetFalse(42);
        bd.SetTrue(99);
        bd.SetTrue(42);

        Assert.Equal(2, bd.Count);
        Assert.Equal(0, bd.FalseCount);
        Assert.Equal(2, bd.TrueCount);
        Assert.Empty(bd.CollectFalseKeys());
        var trueKeys = bd.CollectTrueKeys();
        Assert.Contains(42, trueKeys);
        Assert.Contains(99, trueKeys);
    }

    // ────────── 哈希冲突 ──────────

    [Fact]
    public void HashCollisions_HandledCorrectly() {
        var bd = CreateInt(new FixedHashComparer());
        bd.SetFalse(1);
        bd.SetFalse(2);
        bd.SetTrue(3);

        Assert.Equal(3, bd.Count);
        Assert.Equal(2, bd.FalseCount);
        Assert.Equal(1, bd.TrueCount);

        bd.Remove(2);
        Assert.Equal(2, bd.Count);
        Assert.Equal(1, bd.FalseCount);
        Assert.Equal(1, bd.TrueCount);
        Assert.Equal([1], bd.CollectFalseKeys());
        Assert.Equal([3], bd.CollectTrueKeys());
    }

    [Fact]
    public void HashCollisions_MoveKey_Correctly() {
        var bd = CreateInt(new FixedHashComparer());
        bd.SetFalse(1);
        bd.SetFalse(2);
        bd.SetTrue(2); // 碰撞下的移动
        Assert.Equal(2, bd.Count);
        Assert.Equal(1, bd.FalseCount);
        Assert.Equal(1, bd.TrueCount);
        Assert.Equal([1], bd.CollectFalseKeys());
        Assert.Equal([2], bd.CollectTrueKeys());
    }

    // ────────── Remove 后再添加（与扩容交叉） ──────────

    [Fact]
    public void RemoveAndReAdd_WithResize() {
        var bd = CreateInt();
        for (int i = 0; i < 8; i++) { bd.SetFalse(i); }

        for (int i = 0; i < 4; i++) { bd.Remove(i); }
        Assert.Equal(4, bd.Count);

        for (int i = 100; i < 104; i++) { bd.SetTrue(i); }
        Assert.Equal(8, bd.Count);
        Assert.Equal(4, bd.FalseCount);
        Assert.Equal(4, bd.TrueCount);

        var falseKeys = bd.CollectFalseKeys();
        for (int i = 4; i < 8; i++) { Assert.Contains(i, falseKeys); }
        var trueKeys = bd.CollectTrueKeys();
        for (int i = 100; i < 104; i++) { Assert.Contains(i, trueKeys); }
    }

    // ────────── Helpers ──────────

    private static void AssertCountInvariant<TKey>(ITestBoolDivision<TKey> bd) where TKey : notnull {
        Assert.Equal(bd.Count, bd.FalseCount + bd.TrueCount);
    }

    /// <summary>所有 key 返回相同哈希码，制造最大碰撞。</summary>
    protected class FixedHashComparer : IEqualityComparer<int> {
        public bool Equals(int x, int y) => x == y;
        public int GetHashCode(int obj) => 42;
    }
}
