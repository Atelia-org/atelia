using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using static Atelia.StateJournal.Tests.TestHelper;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DurableDict _dirtyKeys 精确追踪测试。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[S-DIRTYKEYS-TRACKING-EXACT]</c></item>
/// </list>
/// </remarks>
public class DurableDictDirtyTrackingTests {

    // [S-DIRTYKEYS-TRACKING-EXACT]: _dirtyKeys 精确追踪变更

    /// <summary>
    /// Set 回原值后 HasChanges 变为 false。
    /// </summary>
    /// <remarks>
    /// 场景：committed[1] = 100 → Set(1, 200) → Set(1, 100) → HasChanges == false
    /// </remarks>
    [Fact]
    public void DirtyKeys_SetBackToOriginalValue_HasChangesBecomeFalse() {
        // Arrange: committed[1] = 100
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100));
        dict.HasChanges.Should().BeFalse();

        // Act: 修改后再恢复
        dict.Set(1, 200);
        dict.HasChanges.Should().BeTrue();

        dict.Set(1, 100);  // 回到原值

        // Assert
        dict.HasChanges.Should().BeFalse();
        dict.State.Should().Be(DurableObjectState.PersistentDirty); // 状态不回退
    }

    /// <summary>
    /// Remove 新 key（未提交）后 HasChanges 变为 false。
    /// </summary>
    /// <remarks>
    /// 场景：空 dict → Set(1, 100) → Remove(1) → HasChanges == false
    /// </remarks>
    [Fact]
    public void DirtyKeys_RemoveNewKey_HasChangesBecomeFalse() {
        // Arrange: 新建的 dict（无 committed）
        var (dict, ws) = CreateDurableDict();
        dict.HasChanges.Should().BeFalse();

        // Act
        dict.Set(1, 100);
        dict.HasChanges.Should().BeTrue();

        dict.Remove(1);  // 删除新 key

        // Assert
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Remove committed key 后 HasChanges 为 true。
    /// </summary>
    /// <remarks>
    /// 场景：committed[1] = 100 → Remove(1) → HasChanges == true
    /// </remarks>
    [Fact]
    public void DirtyKeys_RemoveCommittedKey_HasChangesIsTrue() {
        // Arrange: committed[1] = 100
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100));

        // Act
        dict.Remove(1);

        // Assert
        dict.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// Set + Remove + Set 恢复后 HasChanges 变为 false。
    /// </summary>
    /// <remarks>
    /// 场景：committed[1] = 100 → Remove(1) → Set(1, 100) → HasChanges == false
    /// </remarks>
    [Fact]
    public void DirtyKeys_RemoveThenSetOriginal_HasChangesBecomeFalse() {
        // Arrange: committed[1] = 100
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100));

        // Act
        dict.Remove(1);
        dict.HasChanges.Should().BeTrue();

        dict.Set(1, 100);  // 恢复原值

        // Assert
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Get 操作不污染 _dirtyKeys。
    /// </summary>
    [Fact]
    public void DirtyKeys_GetOperations_DoNotPollute() {
        // Arrange: committed[1] = 100
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100));
        dict.HasChanges.Should().BeFalse();

        // Act & Assert: 各种读操作都不应改变 HasChanges
        dict.TryGetValue(1, out _);
        dict.HasChanges.Should().BeFalse();

        dict.ContainsKey(1);
        dict.HasChanges.Should().BeFalse();

        _ = dict[1];
        dict.HasChanges.Should().BeFalse();

        _ = dict.Count;
        dict.HasChanges.Should().BeFalse();

        _ = dict.Keys.ToList();
        dict.HasChanges.Should().BeFalse();

        _ = dict.Entries.ToList();
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 往返测试：Set → Commit → Set 相同值 → HasChanges == false。
    /// </summary>
    [Fact]
    public void DirtyKeys_RoundTrip_SetSameValueAfterCommit_HasChangesIsFalse() {
        // Arrange
        var (dict, ws) = CreateDurableDict();
        dict.Set(1, 100);
        dict.OnCommitSucceeded();  // 现在 committed[1] = 100
        dict.HasChanges.Should().BeFalse();

        // Act: 设置相同的值
        dict.Set(1, 100);

        // Assert
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 多个 key 混合追踪。
    /// </summary>
    [Fact]
    public void DirtyKeys_MultipleKeys_MixedTracking() {
        // Arrange: committed[1] = 100, [2] = 200
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100), (2, (object?)200));

        // Act & Assert
        // 修改 key=1
        dict.Set(1, 999);
        dict.HasChanges.Should().BeTrue();

        // 恢复 key=1 到原值，但 key=2 未动 → HasChanges == false
        dict.Set(1, 100);
        dict.HasChanges.Should().BeFalse();

        // 修改 key=2
        dict.Set(2, 888);
        dict.HasChanges.Should().BeTrue();

        // 新增 key=3
        dict.Set(3, 300);
        dict.HasChanges.Should().BeTrue();

        // 删除新增的 key=3，但 key=2 仍脏 → HasChanges == true
        dict.Remove(3);
        dict.HasChanges.Should().BeTrue();

        // 恢复 key=2 → HasChanges == false
        dict.Set(2, 200);
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Remove 不存在的 key 不应改变 HasChanges。
    /// </summary>
    [Fact]
    public void DirtyKeys_RemoveNonExistent_DoesNotChangeHasChanges() {
        // Arrange
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100));
        dict.HasChanges.Should().BeFalse();

        // Act: 删除不存在的 key
        var result = dict.Remove(999);

        // Assert
        result.Should().BeFalse();
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Set 到 null 值的精确追踪。
    /// </summary>
    [Fact]
    public void DirtyKeys_SetNull_TracksCorrectly() {
        // Arrange: committed[1] = null
        var (dict, ws) = CreateCleanDurableDict((1, (object?)null));
        dict.HasChanges.Should().BeFalse();

        // Act: 修改为非 null
        dict.Set(1, "hello");
        dict.HasChanges.Should().BeTrue();

        // Act: 恢复为 null
        dict.Set(1, null);
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 新对象（TransientDirty）的 Set/Remove 追踪。
    /// </summary>
    [Fact]
    public void DirtyKeys_TransientDirty_SetAndRemove() {
        // Arrange: 新对象
        var (dict, ws) = CreateDurableDict();
        dict.State.Should().Be(DurableObjectState.TransientDirty);
        dict.HasChanges.Should().BeFalse();

        // Act & Assert
        dict.Set(1, 100);
        dict.HasChanges.Should().BeTrue();

        dict.Set(2, 200);
        dict.HasChanges.Should().BeTrue();

        // 删除一个
        dict.Remove(1);
        dict.HasChanges.Should().BeTrue();  // key=2 仍脏

        // 删除另一个
        dict.Remove(2);
        dict.HasChanges.Should().BeFalse();  // 回到初始空状态
    }

    /// <summary>
    /// 复杂场景：多次 Set/Remove 同一 key。
    /// </summary>
    [Fact]
    public void DirtyKeys_ComplexScenario_SameKeyMultipleOperations() {
        // Arrange: committed[1] = 100
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100));

        // Act & Assert: 一系列操作
        dict.Set(1, 200);  // 脏
        dict.HasChanges.Should().BeTrue();

        dict.Set(1, 300);  // 仍脏（不同于 committed）
        dict.HasChanges.Should().BeTrue();

        dict.Remove(1);    // 脏（删除 committed key）
        dict.HasChanges.Should().BeTrue();

        dict.Set(1, 400);  // 脏（新值不同于 committed）
        dict.HasChanges.Should().BeTrue();

        dict.Set(1, 100);  // 回到原值 → 不脏
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 验证 WritePendingDiff 输出与 HasChanges 一致。
    /// </summary>
    [Fact]
    public void DirtyKeys_WritePendingDiff_ConsistentWithHasChanges() {
        // Arrange: committed[1] = 100
        var (dict, ws) = CreateCleanDurableDict((1, (object?)100L));

        // Case 1: 无变更 → 空 diff
        var buffer1 = new ArrayBufferWriter<byte>();
        dict.WritePendingDiff(buffer1);
        var reader1 = new DiffPayloadReader(buffer1.WrittenSpan);
        reader1.PairCount.Should().Be(0);
        dict.HasChanges.Should().BeFalse();

        // Case 2: 修改后恢复 → 空 diff
        dict.Set(1, 200L);
        dict.Set(1, 100L);
        var buffer2 = new ArrayBufferWriter<byte>();
        dict.WritePendingDiff(buffer2);
        var reader2 = new DiffPayloadReader(buffer2.WrittenSpan);
        reader2.PairCount.Should().Be(0);
        dict.HasChanges.Should().BeFalse();

        // Case 3: 实际变更 → 非空 diff
        dict.Set(1, 999L);
        var buffer3 = new ArrayBufferWriter<byte>();
        dict.WritePendingDiff(buffer3);
        var reader3 = new DiffPayloadReader(buffer3.WrittenSpan);
        reader3.PairCount.Should().Be(1);
        dict.HasChanges.Should().BeTrue();
    }
}
