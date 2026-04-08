using Atelia.StateJournal.NodeContainers;
using Xunit;

namespace Atelia.StateJournal.Tests.NodeContainers;

public class ExperimentalNodeArenaSkipListMapTests {
    [Fact]
    public void UpsertAndLookup_MaintainSortedLeafOrder() {
        var map = new ExperimentalNodeArenaSkipListMap();

        foreach (int key in new[] { 7, 3, 11, 5, 1, 9 }) {
            map.Upsert(key, $"v{key}");
        }

        Assert.Equal(6, map.Count);
        Assert.True(map.TryGet(5, out string? value));
        Assert.Equal("v5", value);
        Assert.False(map.TryGet(6, out _));

        Assert.Equal(
            new[] {
                new KeyValuePair<int, string?>(5, "v5"),
                new KeyValuePair<int, string?>(7, "v7"),
                new KeyValuePair<int, string?>(9, "v9"),
                new KeyValuePair<int, string?>(11, "v11"),
            },
            map.ReadAscendingFrom(5, 8)
        );

        Assert.True(map.TryGetLowerBound(6, out var lowerBound));
        Assert.Equal(new KeyValuePair<int, string?>(7, "v7"), lowerBound);
        Assert.True(map.TryGetNext(7, out var next));
        Assert.Equal(new KeyValuePair<int, string?>(9, "v9"), next);
    }

    [Fact]
    public void CommitAndRevert_RestoreCommittedSnapshot() {
        var map = new ExperimentalNodeArenaSkipListMap();

        map.Upsert(2, "two");
        map.Upsert(4, "four");
        map.Upsert(6, "six");
        map.Commit();

        map.Upsert(4, "FOUR");
        map.Upsert(5, "five");

        Assert.True(map.TryGet(4, out string? updated));
        Assert.Equal("FOUR", updated);
        Assert.True(map.TryGet(5, out string? inserted));
        Assert.Equal("five", inserted);
        Assert.Equal(4, map.Count);

        map.Revert();

        Assert.True(map.TryGet(4, out string? reverted));
        Assert.Equal("four", reverted);
        Assert.False(map.TryGet(5, out _));
        Assert.Equal(3, map.Count);
    }

    [Fact]
    public void CollectBuilderNodes_CompactsDraftNodesWithoutBreakingReads() {
        var map = new ExperimentalNodeArenaSkipListMap();

        for (int i = 0; i < 20; ++i) {
            map.Upsert(i * 2, $"v{i}");
        }
        map.Commit();

        map.Upsert(15, "draft-15");
        map.Upsert(21, "draft-21");

        int branchBeforeCollect = map.DebugBranchNodeCount;
        int leafBeforeCollect = map.DebugLeafNodeCount;

        map.CollectBuilderNodes();

        Assert.True(map.TryGet(15, out string? updated));
        Assert.Equal("draft-15", updated);
        Assert.True(map.TryGet(21, out string? inserted));
        Assert.Equal("draft-21", inserted);
        Assert.Equal(branchBeforeCollect, map.DebugBranchNodeCount);
        Assert.Equal(leafBeforeCollect, map.DebugLeafNodeCount);
        Assert.True(map.DebugBranchNodeCount >= map.DebugCommittedBranchNodeCount);
        Assert.True(map.DebugLeafNodeCount >= map.DebugCommittedLeafNodeCount);
    }

    [Fact]
    public void DuplicateUpsert_UpdatesInPlaceWithoutGrowingCount() {
        var map = new ExperimentalNodeArenaSkipListMap();

        map.Upsert(8, "v1");
        map.Commit();

        int branchBefore = map.DebugBranchNodeCount;
        int leafBefore = map.DebugLeafNodeCount;

        map.Upsert(8, "v2");
        map.Upsert(8, "v3");

        Assert.Equal(1, map.Count);
        Assert.True(map.TryGet(8, out string? value));
        Assert.Equal("v3", value);
        Assert.Equal(branchBefore, map.DebugBranchNodeCount);
        Assert.Equal(leafBefore, map.DebugLeafNodeCount);
    }
}
