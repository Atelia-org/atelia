using Atelia.StateJournal.Internal;
using Xunit;

namespace Atelia.StateJournal.Tests.Internal;

public class ExperimentalCowBTreeIntMapTests {
    [Fact]
    public void InsertAndLookup_WorkAcrossMultipleSplits() {
        var map = new ExperimentalCowBTreeIntMap();

        for (int i = 0; i < 20; ++i) {
            map.Upsert(i, i * 10);
        }

        Assert.Equal(20, map.Count);
        for (int i = 0; i < 20; ++i) {
            Assert.True(map.TryGet(i, out int value));
            Assert.Equal(i * 10, value);
        }
        Assert.False(map.TryGet(999, out _));

        var tail = map.ReadAscendingFrom(14, 4);
        Assert.Equal(
            new[] {
                new KeyValuePair<int, int>(14, 140),
                new KeyValuePair<int, int>(15, 150),
                new KeyValuePair<int, int>(16, 160),
                new KeyValuePair<int, int>(17, 170),
            },
            tail
        );

        Assert.True(map.TryGetLowerBound(14, out var lowerBound));
        Assert.Equal(new KeyValuePair<int, int>(14, 140), lowerBound);
        Assert.True(map.TryGetLowerBound(14 - 1, out lowerBound));
        Assert.Equal(new KeyValuePair<int, int>(13, 130), lowerBound);
        Assert.True(map.TryGetNext(17, out var next));
        Assert.Equal(new KeyValuePair<int, int>(18, 180), next);
        Assert.False(map.TryGetLowerBound(1000, out _));
        Assert.False(map.TryGetNext(19, out _));
    }

    [Fact]
    public void CommitAndRevert_RestoreCommittedSnapshot() {
        var map = new ExperimentalCowBTreeIntMap();
        for (int i = 0; i < 8; ++i) {
            map.Upsert(i, i);
        }
        map.Commit();

        map.Upsert(3, 300);
        map.Upsert(8, 800);

        Assert.True(map.TryGet(3, out int updated));
        Assert.Equal(300, updated);
        Assert.True(map.TryGet(8, out int inserted));
        Assert.Equal(800, inserted);
        Assert.Equal(9, map.Count);

        map.Revert();

        Assert.True(map.TryGet(3, out int reverted));
        Assert.Equal(3, reverted);
        Assert.False(map.TryGet(8, out _));
        Assert.Equal(8, map.Count);
    }

    [Fact]
    public void RepeatedDraftUpdates_ReuseMutablePath() {
        var map = new ExperimentalCowBTreeIntMap();
        for (int i = 0; i < 12; ++i) {
            map.Upsert(i, i);
        }
        map.Commit();

        int before = map.DebugNodeCount;
        map.Upsert(5, 500);
        int afterFirstUpdate = map.DebugNodeCount;
        map.Upsert(5, 600);
        int afterSecondUpdate = map.DebugNodeCount;

        Assert.Equal(before, afterFirstUpdate);
        Assert.Equal(afterFirstUpdate, afterSecondUpdate);

        map.CollectBuilderNodes();

        Assert.True(map.TryGet(5, out int value));
        Assert.Equal(600, value);
        Assert.Equal(before, map.DebugNodeCount);
        Assert.True(map.DebugNodeCount >= map.DebugCommittedNodeCount);
    }

    [Fact]
    public void LowerBound_ReflectsLatestDraftUpdates() {
        var map = new ExperimentalCowBTreeIntMap();
        for (int i = 0; i < 10; ++i) {
            map.Upsert(i * 2, i);
        }
        map.Commit();

        map.Upsert(9, 900);
        map.Upsert(10, 1000);

        Assert.Equal(
            new[] {
                new KeyValuePair<int, int>(0, 0),
                new KeyValuePair<int, int>(2, 1),
                new KeyValuePair<int, int>(4, 2),
                new KeyValuePair<int, int>(6, 3),
                new KeyValuePair<int, int>(8, 4),
                new KeyValuePair<int, int>(9, 900),
                new KeyValuePair<int, int>(10, 1000),
                new KeyValuePair<int, int>(12, 6),
                new KeyValuePair<int, int>(14, 7),
                new KeyValuePair<int, int>(16, 8),
                new KeyValuePair<int, int>(18, 9),
            },
            map.ReadAscendingFrom(0, 20)
        );

        Assert.True(map.TryGetLowerBound(9, out var lowerBound));
        Assert.Equal(new KeyValuePair<int, int>(9, 900), lowerBound);

        Assert.True(map.TryGetNext(9, out var next));
        Assert.Equal(new KeyValuePair<int, int>(10, 1000), next);

        map.Revert();

        Assert.True(map.TryGetLowerBound(9, out lowerBound));
        Assert.Equal(new KeyValuePair<int, int>(10, 5), lowerBound);
    }
}
