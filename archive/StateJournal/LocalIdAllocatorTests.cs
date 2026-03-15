using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class LocalIdAllocatorTests {
    [Fact]
    public void Empty_Keys_StartsFrom1() {
        var allocator = LocalIdAllocator.FromKeys(Array.Empty<uint>());
        Assert.Equal(1u, allocator.NextId);

        var id1 = allocator.Allocate();
        Assert.Equal(1u, id1.Value);
        var id2 = allocator.Allocate();
        Assert.Equal(2u, id2.Value);
    }

    [Fact]
    public void Contiguous_Keys_AllocatesFromHighWater() {
        var allocator = LocalIdAllocator.FromKeys(new uint[] { 1, 2, 3 });
        Assert.Equal(4u, allocator.NextId);

        var id = allocator.Allocate();
        Assert.Equal(4u, id.Value);
    }

    [Fact]
    public void SingleHole_IsReused() {
        // keys: {1, 3} → hole at 2
        var allocator = LocalIdAllocator.FromKeys(new uint[] { 1, 3 });
        Assert.Equal(4u, allocator.NextId);

        var id = allocator.Allocate();
        Assert.Equal(2u, id.Value); // single hole reused first
    }

    [Fact]
    public void MultipleHoles_AreReused() {
        // keys: {1, 4, 7} → hole at 2, hole at 3, hole at 5, hole at 6
        // holes: single holes: {2, 5}; hole spans: {(3,1), (6,1)} —
        // Actually: gap 1→4 is {2,3} (length 2 → HoleSpan), gap 4→7 is {5,6} (length 2 → HoleSpan)
        var allocator = LocalIdAllocator.FromKeys(new uint[] { 1, 4, 7 });
        Assert.Equal(8u, allocator.NextId);

        // All holes should be allocated before nextId
        var ids = new HashSet<uint>();
        for (int i = 0; i < 4; i++) {
            ids.Add(allocator.Allocate().Value);
        }
        // Should contain 2, 3, 5, 6 (from holes)
        Assert.Contains(2u, ids);
        Assert.Contains(3u, ids);
        Assert.Contains(5u, ids);
        Assert.Contains(6u, ids);

        // Next allocation should be from high water
        var nextId = allocator.Allocate();
        Assert.Equal(8u, nextId.Value);
    }

    [Fact]
    public void Unsorted_Keys_AreHandledCorrectly() {
        // keys provided out of order
        var allocator = LocalIdAllocator.FromKeys(new uint[] { 3, 1, 5 });
        Assert.Equal(6u, allocator.NextId);

        // holes: 2 (single), 4 (single)
        var ids = new HashSet<uint>();
        ids.Add(allocator.Allocate().Value);
        ids.Add(allocator.Allocate().Value);
        Assert.Contains(2u, ids);
        Assert.Contains(4u, ids);

        // Now should allocate from high water
        Assert.Equal(6u, allocator.Allocate().Value);
    }

    [Fact]
    public void LargeGap_UsesHoleSpan() {
        // keys: {1, 100} → holes 2..99 (length 98, stored as HoleSpan)
        var allocator = LocalIdAllocator.FromKeys(new uint[] { 1, 100 });
        Assert.Equal(101u, allocator.NextId);

        // First 98 allocations should come from the hole span
        for (uint i = 2; i <= 99; i++) {
            var id = allocator.Allocate();
            Assert.True(id.Value >= 2 && id.Value <= 99, $"Expected hole value, got {id.Value}");
        }

        // Now should come from high water
        Assert.Equal(101u, allocator.Allocate().Value);
    }

    [Fact]
    public void Keys_Starting_From_Nonone_HasLeadingHole() {
        // keys: {3, 4} → holes: 1,2
        var allocator = LocalIdAllocator.FromKeys(new uint[] { 3, 4 });
        Assert.Equal(5u, allocator.NextId);

        // 1 and 2 are holes
        var ids = new HashSet<uint>();
        ids.Add(allocator.Allocate().Value);
        ids.Add(allocator.Allocate().Value);
        Assert.Contains(1u, ids);
        Assert.Contains(2u, ids);

        Assert.Equal(5u, allocator.Allocate().Value);
    }

    [Fact]
    public void MaxKey_Wraps_NextId_To_Zero_But_Holes_Still_Usable() {
        // sorted[^1] == uint.MaxValue → nextId wraps to 0 (sentinel for exhausted)
        var allocator = LocalIdAllocator.FromKeys(new uint[] { 1, uint.MaxValue });
        Assert.Equal(0u, allocator.NextId);

        // Hole span [2, uint.MaxValue) should still be usable
        var id = allocator.Allocate();
        Assert.True(id.Value >= 2 && id.Value < uint.MaxValue, $"Expected hole value, got {id.Value}");
    }

    [Fact]
    public void Allocate_Throws_When_NextId_Exhausted_And_NoHoles() {
        // Directly construct allocator with nextId=0 (sentinel) and no holes
        var allocator = new LocalIdAllocator(0);

        Assert.Throws<LocalIdExhaustedException>(() => allocator.Allocate());
    }

    [Fact]
    public void Allocate_Throws_After_HighWater_Wraps() {
        // nextId = uint.MaxValue, no holes → allocates MaxValue, wraps to 0, next throws
        var allocator = new LocalIdAllocator(uint.MaxValue);

        var id = allocator.Allocate();
        Assert.Equal(uint.MaxValue, id.Value);

        // nextId has wrapped to 0 → should throw
        Assert.Throws<LocalIdExhaustedException>(() => allocator.Allocate());
    }
}
