using System.Runtime.InteropServices;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

[Collection("ValueBox")]
public class DictChangeTrackerTests {
    private static int Bits64Count => ValuePools.OfBits64.Count;

    [Fact]
    public void AfterUpsert_NoChange_CanonicalizesCurrent_AndReleasesTemporarySlot() {
        var tracker = new DictChangeTracker<byte, ValueBox>();
        const byte key = 1;

        var committed = ValueBox.Int64Face.From(long.MaxValue);
        tracker.Current[key] = committed;
        tracker.AfterUpsert<ValueBoxHelper>(key, committed);
        tracker.Commit<ValueBoxHelper>();

        int countAfterCommit = Bits64Count;

        // 模拟真实 Upsert 流程：通过 ref 获取 slot，用 Update 尝试更新
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out _);
        bool changed = ValueBox.Int64Face.UpdateOrInit(ref slot, long.MaxValue);
        Assert.False(changed); // 值未变，Update 返回 false

        // Update 返回 false 时跳过 AfterUpsert，slot 保持 frozen 不变
        Assert.False(tracker.HasChanges);
        Assert.Equal(countAfterCommit, Bits64Count); // 未分配新 slot

        ValueBox current = tracker.Current[key];
        Assert.Equal(ValueBox.Freeze(current).GetBits(), current.GetBits()); // 仍是 frozen
    }

    [Fact]
    public void AfterRemove_DirtyInsertedHeapValue_ReleasesSlot_AndClearsDirty() {
        var tracker = new DictChangeTracker<byte, ValueBox>();
        const byte key = 2;

        int before = Bits64Count;

        var inserted = ValueBox.Int64Face.From(long.MaxValue);
        tracker.Current[key] = inserted;
        tracker.AfterUpsert<ValueBoxHelper>(key, inserted);

        Assert.True(tracker.HasChanges);
        Assert.Equal(before + 1, Bits64Count);

        bool removed = tracker.Current.Remove(key, out var removedValue);
        Assert.True(removed);
        tracker.AfterRemove<ValueBoxHelper>(key, removedValue);

        Assert.False(tracker.HasChanges);
        Assert.Equal(before, Bits64Count);
    }

    [Fact]
    public void AfterRemove_AfterNoChangeUpsert_DoesNotLeakExtraSlot() {
        var tracker = new DictChangeTracker<byte, ValueBox>();
        const byte key = 3;

        var committed = ValueBox.Int64Face.From(long.MaxValue);
        tracker.Current[key] = committed;
        tracker.AfterUpsert<ValueBoxHelper>(key, committed);
        tracker.Commit<ValueBoxHelper>();
        int countAfterCommit = Bits64Count;

        // 模拟真实 Upsert 流程：Update 检测值相同 → 返回 false → 跳过 AfterUpsert
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out _);
        bool changed = ValueBox.Int64Face.UpdateOrInit(ref slot, long.MaxValue);
        Assert.False(changed);
        // AfterUpsert 未被调用，状态不变
        Assert.False(tracker.HasChanges);

        bool removed = tracker.Current.Remove(key, out var removedValue);
        Assert.True(removed);
        tracker.AfterRemove<ValueBoxHelper>(key, removedValue);

        Assert.True(tracker.HasChanges);
        Assert.Equal(countAfterCommit, Bits64Count);
    }
}
