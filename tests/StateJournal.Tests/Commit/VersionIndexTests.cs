using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Commit;

/// <summary>
/// VersionIndex 测试。
/// </summary>
/// <remarks>
/// 对应条款：<c>[F-VERSIONINDEX-REUSE-DURABLEDICT]</c>
/// </remarks>
public class VersionIndexTests {

    /// <summary>
    /// 辅助方法：将 Dictionary&lt;ulong, ulong?&gt; 转换为 Dictionary&lt;ulong, object?&gt;。
    /// </summary>
    private static Dictionary<ulong, object?> ToObjectDict(Dictionary<ulong, ulong?> source)
        => source.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    #region Well-Known ObjectId 测试

    /// <summary>
    /// VersionIndex 使用 Well-Known ObjectId 0。
    /// </summary>
    [Fact]
    public void VersionIndex_HasWellKnownObjectId() {
        // Arrange & Act
        var index = new VersionIndex();

        // Assert
        index.ObjectId.Should().Be(0);
        VersionIndex.WellKnownObjectId.Should().Be(0);
    }

    #endregion

    #region SetAndGet 基础测试

    /// <summary>
    /// SetObjectVersionPtr 后 TryGetObjectVersionPtr 返回正确的值。
    /// </summary>
    [Fact]
    public void SetAndGet_ObjectVersionPtr() {
        // Arrange
        var index = new VersionIndex();

        // Act
        index.SetObjectVersionPtr(16, 0x1000);
        index.SetObjectVersionPtr(17, 0x2000);

        // Assert
        index.TryGetObjectVersionPtr(16, out var ptr1).Should().BeTrue();
        ptr1.Should().Be(0x1000);

        index.TryGetObjectVersionPtr(17, out var ptr2).Should().BeTrue();
        ptr2.Should().Be(0x2000);
    }

    /// <summary>
    /// TryGetObjectVersionPtr 对不存在的 ObjectId 返回 false。
    /// </summary>
    [Fact]
    public void TryGet_NonExistent_ReturnsFalse() {
        // Arrange
        var index = new VersionIndex();

        // Act & Assert
        index.TryGetObjectVersionPtr(999, out var ptr).Should().BeFalse();
        ptr.Should().Be(0);
    }

    /// <summary>
    /// 多次 SetObjectVersionPtr 覆盖之前的值。
    /// </summary>
    [Fact]
    public void SetMultipleTimes_OverwritesPreviousValue() {
        // Arrange
        var index = new VersionIndex();

        // Act
        index.SetObjectVersionPtr(16, 0x1000);
        index.SetObjectVersionPtr(16, 0x2000);
        index.SetObjectVersionPtr(16, 0x3000);

        // Assert
        index.TryGetObjectVersionPtr(16, out var ptr).Should().BeTrue();
        ptr.Should().Be(0x3000);
    }

    /// <summary>
    /// ObjectIds 枚举返回所有已设置的 ObjectId。
    /// </summary>
    [Fact]
    public void ObjectIds_ReturnsAllSetKeys() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(16, 0x1000);
        index.SetObjectVersionPtr(20, 0x2000);
        index.SetObjectVersionPtr(18, 0x3000);

        // Assert
        index.ObjectIds.Should().BeEquivalentTo(new ulong[] { 16, 18, 20 });
    }

    /// <summary>
    /// Count 返回正确的条目数量。
    /// </summary>
    [Fact]
    public void Count_ReturnsCorrectNumber() {
        // Arrange
        var index = new VersionIndex();

        // Assert - 初始为空
        index.Count.Should().Be(0);

        // Act
        index.SetObjectVersionPtr(16, 0x1000);
        index.SetObjectVersionPtr(17, 0x2000);
        index.SetObjectVersionPtr(18, 0x3000);

        // Assert
        index.Count.Should().Be(3);
    }

    #endregion

    #region ComputeNextObjectId 测试

    /// <summary>
    /// 空 VersionIndex 的 ComputeNextObjectId 返回 16（保留区之后的第一个 ID）。
    /// </summary>
    [Fact]
    public void ComputeNextObjectId_Empty_Returns16() {
        // Arrange
        var index = new VersionIndex();

        // Act & Assert
        index.ComputeNextObjectId().Should().Be(16);
    }

    /// <summary>
    /// 有对象时 ComputeNextObjectId 返回 max(keys) + 1。
    /// </summary>
    [Fact]
    public void ComputeNextObjectId_WithObjects_ReturnsMaxPlusOne() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(16, 0x1000);
        index.SetObjectVersionPtr(20, 0x2000);
        index.SetObjectVersionPtr(18, 0x3000);

        // Act & Assert
        index.ComputeNextObjectId().Should().Be(21);
    }

    /// <summary>
    /// ComputeNextObjectId 不低于 16（保护保留区）。
    /// </summary>
    [Fact]
    public void ComputeNextObjectId_ProtectsReservedRange() {
        // Arrange - 从 committed 加载包含保留区 ObjectId 的索引
        var committed = new Dictionary<ulong, ulong?>
        {
            { 0, 0x100 },   // VersionIndex 自身的指针（特殊情况）
            { 5, 0x200 },   // 另一个保留区 ID
        };
        var index = new VersionIndex(ToObjectDict(committed));

        // Act & Assert - 仍然返回 16
        index.ComputeNextObjectId().Should().Be(16);
    }

    /// <summary>
    /// ComputeNextObjectId 正确处理大 ObjectId。
    /// </summary>
    [Fact]
    public void ComputeNextObjectId_LargeObjectId() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(1000000, 0x1000);

        // Act & Assert
        index.ComputeNextObjectId().Should().Be(1000001);
    }

    #endregion

    #region IDurableObject 状态测试

    /// <summary>
    /// 新创建的 VersionIndex 处于 TransientDirty 状态。
    /// </summary>
    [Fact]
    public void VersionIndex_IsTransientDirty_WhenNew() {
        // Arrange & Act
        var index = new VersionIndex();

        // Assert
        index.State.Should().Be(DurableObjectState.TransientDirty);
    }

    /// <summary>
    /// 从 committed 加载的 VersionIndex 处于 Clean 状态。
    /// </summary>
    [Fact]
    public void VersionIndex_IsClean_WhenLoadedFromCommitted() {
        // Arrange
        var committed = new Dictionary<ulong, ulong?> { { 16, 0x1000UL } };

        // Act
        var index = new VersionIndex(ToObjectDict(committed));

        // Assert
        index.State.Should().Be(DurableObjectState.Clean);
        index.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// SetObjectVersionPtr 后 HasChanges 为 true。
    /// </summary>
    [Fact]
    public void VersionIndex_HasChanges_AfterSet() {
        // Arrange
        var index = new VersionIndex();

        // Act
        index.SetObjectVersionPtr(16, 0x1000);

        // Assert
        index.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// 新创建但未修改的 VersionIndex HasChanges 为 false。
    /// </summary>
    [Fact]
    public void VersionIndex_NoChanges_WhenNewAndUnmodified() {
        // Arrange & Act
        var index = new VersionIndex();

        // Assert
        index.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// Clean 状态修改后变为 PersistentDirty。
    /// </summary>
    [Fact]
    public void VersionIndex_Clean_AfterSet_BecomesPersistentDirty() {
        // Arrange
        var committed = new Dictionary<ulong, ulong?> { { 16, 0x1000UL } };
        var index = new VersionIndex(ToObjectDict(committed));
        index.State.Should().Be(DurableObjectState.Clean);

        // Act
        index.SetObjectVersionPtr(17, 0x2000);

        // Assert
        index.State.Should().Be(DurableObjectState.PersistentDirty);
        index.HasChanges.Should().BeTrue();
    }

    #endregion

    #region WritePendingDiff 测试

    /// <summary>
    /// WritePendingDiff 生成可以被 DiffPayloadReader 解析的 payload。
    /// </summary>
    [Fact]
    public void VersionIndex_WritePendingDiff_ProducesValidPayload() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(16, 0x1000);
        index.SetObjectVersionPtr(17, 0x2000);

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        index.WritePendingDiff(buffer);

        // Assert - 验证可以用 DiffPayloadReader 读取
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(2);
        reader.HasError.Should().BeFalse();

        // 读取所有对
        var pairs = new List<(ulong Key, ValueType Type)>();
        while (reader.TryReadNext(out var key, out var valueType, out _).Value) {
            pairs.Add((key, valueType));
        }

        pairs.Should().HaveCount(2);
        // 由于 DurableDict 写入的是 VarInt 或 Null
        // ulong? 被当作 long? 处理（通过 VarInt 编码）
    }

    /// <summary>
    /// WritePendingDiff 后状态不变（[S-POSTCOMMIT-WRITE-ISOLATION]）。
    /// </summary>
    [Fact]
    public void VersionIndex_WritePendingDiff_DoesNotChangeState() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(16, 0x1000);
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        index.WritePendingDiff(buffer);

        // Assert
        index.HasChanges.Should().BeTrue();
        index.State.Should().Be(DurableObjectState.TransientDirty);
    }

    /// <summary>
    /// 空变更时 WritePendingDiff 输出 PairCount=0。
    /// </summary>
    [Fact]
    public void VersionIndex_WritePendingDiff_EmptyChanges() {
        // Arrange
        var committed = new Dictionary<ulong, ulong?> { { 16, 0x1000UL } };
        var index = new VersionIndex(ToObjectDict(committed));
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        index.WritePendingDiff(buffer);

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(0);
    }

    #endregion

    #region OnCommitSucceeded 测试

    /// <summary>
    /// OnCommitSucceeded 后状态变为 Clean。
    /// </summary>
    [Fact]
    public void VersionIndex_OnCommitSucceeded_StateBecomesClean() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(16, 0x1000);
        index.State.Should().Be(DurableObjectState.TransientDirty);

        // Act
        index.OnCommitSucceeded();

        // Assert
        index.State.Should().Be(DurableObjectState.Clean);
        index.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// OnCommitSucceeded 后值仍可访问。
    /// </summary>
    [Fact]
    public void VersionIndex_OnCommitSucceeded_ValuesStillAccessible() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(16, 0x1000);
        index.SetObjectVersionPtr(17, 0x2000);

        // Act
        index.OnCommitSucceeded();

        // Assert
        index.TryGetObjectVersionPtr(16, out var ptr1).Should().BeTrue();
        ptr1.Should().Be(0x1000);

        index.TryGetObjectVersionPtr(17, out var ptr2).Should().BeTrue();
        ptr2.Should().Be(0x2000);

        index.Count.Should().Be(2);
    }

    #endregion

    #region DiscardChanges 测试

    /// <summary>
    /// TransientDirty 状态调用 DiscardChanges 后变为 Detached。
    /// </summary>
    [Fact]
    public void VersionIndex_DiscardChanges_TransientDirty_BecomesDetached() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(16, 0x1000);
        index.State.Should().Be(DurableObjectState.TransientDirty);

        // Act
        index.DiscardChanges();

        // Assert
        index.State.Should().Be(DurableObjectState.Detached);
    }

    /// <summary>
    /// PersistentDirty 状态调用 DiscardChanges 后重置为 Clean。
    /// </summary>
    [Fact]
    public void VersionIndex_DiscardChanges_PersistentDirty_ResetsToClean() {
        // Arrange
        var committed = new Dictionary<ulong, ulong?> { { 16, 0x1000UL } };
        var index = new VersionIndex(ToObjectDict(committed));
        index.SetObjectVersionPtr(16, 0x9999);  // 修改
        index.SetObjectVersionPtr(17, 0x2000);  // 新增
        index.State.Should().Be(DurableObjectState.PersistentDirty);

        // Act
        index.DiscardChanges();

        // Assert
        index.State.Should().Be(DurableObjectState.Clean);
        index.HasChanges.Should().BeFalse();

        // 验证回到 committed 状态
        index.TryGetObjectVersionPtr(16, out var ptr).Should().BeTrue();
        ptr.Should().Be(0x1000);  // 恢复原值

        index.TryGetObjectVersionPtr(17, out _).Should().BeFalse();  // 新增被丢弃
        index.Count.Should().Be(1);
    }

    /// <summary>
    /// Clean 状态调用 DiscardChanges 是 No-op。
    /// </summary>
    [Fact]
    public void VersionIndex_DiscardChanges_Clean_IsNoop() {
        // Arrange
        var committed = new Dictionary<ulong, ulong?> { { 16, 0x1000UL } };
        var index = new VersionIndex(ToObjectDict(committed));
        index.State.Should().Be(DurableObjectState.Clean);

        // Act
        index.DiscardChanges();

        // Assert
        index.State.Should().Be(DurableObjectState.Clean);
        index.TryGetObjectVersionPtr(16, out var ptr).Should().BeTrue();
        ptr.Should().Be(0x1000);
    }

    #endregion

    #region 从 committed 恢复测试

    /// <summary>
    /// 从 committed 恢复后可以正确读取值。
    /// </summary>
    [Fact]
    public void VersionIndex_FromCommitted_CanReadValues() {
        // Arrange
        var committed = new Dictionary<ulong, ulong?>
        {
            { 16, 0x1000UL },
            { 17, 0x2000UL },
            { 20, 0x3000UL }
        };

        // Act
        var index = new VersionIndex(ToObjectDict(committed));

        // Assert
        index.TryGetObjectVersionPtr(16, out var ptr1).Should().BeTrue();
        ptr1.Should().Be(0x1000);

        index.TryGetObjectVersionPtr(17, out var ptr2).Should().BeTrue();
        ptr2.Should().Be(0x2000);

        index.TryGetObjectVersionPtr(20, out var ptr3).Should().BeTrue();
        ptr3.Should().Be(0x3000);

        index.Count.Should().Be(3);
        index.ComputeNextObjectId().Should().Be(21);
    }

    /// <summary>
    /// 从 committed 恢复后处于 Clean 状态。
    /// </summary>
    [Fact]
    public void VersionIndex_FromCommitted_IsClean() {
        // Arrange
        var committed = new Dictionary<ulong, ulong?> { { 16, 0x1000UL } };

        // Act
        var index = new VersionIndex(ToObjectDict(committed));

        // Assert
        index.State.Should().Be(DurableObjectState.Clean);
        index.HasChanges.Should().BeFalse();
    }

    #endregion

    #region 完整二阶段提交流程测试

    /// <summary>
    /// 完整的二阶段提交流程：Set → WritePendingDiff → OnCommitSucceeded。
    /// </summary>
    [Fact]
    public void TwoPhaseCommit_RoundTrip() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(16, 0x1000);
        index.SetObjectVersionPtr(17, 0x2000);
        index.SetObjectVersionPtr(18, 0x3000);

        // Act - Phase 1: WritePendingDiff
        var buffer = new ArrayBufferWriter<byte>();
        index.WritePendingDiff(buffer);

        // 验证 Phase 1 不改变状态
        index.HasChanges.Should().BeTrue();
        index.State.Should().Be(DurableObjectState.TransientDirty);

        // Act - Phase 2: OnCommitSucceeded
        index.OnCommitSucceeded();

        // Assert
        index.HasChanges.Should().BeFalse();
        index.State.Should().Be(DurableObjectState.Clean);

        // 验证序列化内容
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(3);

        // 验证值仍可访问
        index.TryGetObjectVersionPtr(16, out var ptr1).Should().BeTrue();
        ptr1.Should().Be(0x1000);

        index.TryGetObjectVersionPtr(17, out var ptr2).Should().BeTrue();
        ptr2.Should().Be(0x2000);

        index.TryGetObjectVersionPtr(18, out var ptr3).Should().BeTrue();
        ptr3.Should().Be(0x3000);
    }

    /// <summary>
    /// Commit 后再修改变为 PersistentDirty。
    /// </summary>
    [Fact]
    public void AfterCommit_Modify_BecomesPersistentDirty() {
        // Arrange
        var index = new VersionIndex();
        index.SetObjectVersionPtr(16, 0x1000);
        index.OnCommitSucceeded();
        index.State.Should().Be(DurableObjectState.Clean);

        // Act
        index.SetObjectVersionPtr(17, 0x2000);

        // Assert
        index.State.Should().Be(DurableObjectState.PersistentDirty);
        index.HasChanges.Should().BeTrue();
    }

    #endregion
}
