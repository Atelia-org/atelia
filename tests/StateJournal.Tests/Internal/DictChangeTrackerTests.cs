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

        // 模拟真实 Upsert 流程：通过 ref 获取 slot，用 UpdateBy 做 COW 更新
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out _);
        ValueBox.Int64Face.Update(ref slot, long.MaxValue); // frozen → 新 exclusive slot
        tracker.AfterUpsert<ValueBoxHelper>(key, slot);  // 检测 no-change → 释放 exclusive → 恢复 frozen

        Assert.False(tracker.HasChanges);
        Assert.Equal(countAfterCommit, Bits64Count);

        ValueBox current = tracker.Current[key];
        Assert.Equal(ValueBox.Freeze(current).GetBits(), current.GetBits());
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

        // 模拟真实 Upsert 流程
        ref ValueBox slot = ref CollectionsMarshal.GetValueRefOrAddDefault(tracker.Current, key, out _);
        ValueBox.Int64Face.Update(ref slot, long.MaxValue);
        tracker.AfterUpsert<ValueBoxHelper>(key, slot);
        Assert.False(tracker.HasChanges);

        bool removed = tracker.Current.Remove(key, out var removedValue);
        Assert.True(removed);
        tracker.AfterRemove<ValueBoxHelper>(key, removedValue);

        Assert.True(tracker.HasChanges);
        Assert.Equal(countAfterCommit, Bits64Count);
    }
}
