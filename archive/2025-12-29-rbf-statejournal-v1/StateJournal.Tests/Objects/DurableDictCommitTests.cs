using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using Ws = Atelia.StateJournal.Workspace;
using static Atelia.StateJournal.Tests.TestHelper;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DurableDict 提交操作测试（OnCommitSucceeded 和 DiscardChanges）。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[A-DURABLEDICT-API-SIGNATURES]</c></item>
///   <item><c>[A-DISCARDCHANGES-REVERT-COMMITTED]</c></item>
///   <item><c>[S-TRANSIENT-DISCARD-DETACH]</c></item>
/// </list>
/// </remarks>
public class DurableDictCommitTests {

    /// <summary>
    /// 创建一个 Detached 状态的 DurableDict（通过反射设置状态）。
    /// </summary>
    private static DurableDict CreateDetachedDict<TValue>() {
        var (dict, _) = CreateDurableDict();
        // 使用反射设置基类 DurableObjectBase 中的 _state 为 Detached
        var stateField = typeof(DurableObjectBase).GetField(
            "_state",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        stateField!.SetValue(dict, DurableObjectState.Detached);
        return dict;
    }

    #region OnCommitSucceeded 测试

    /// <summary>
    /// OnCommitSucceeded 后 HasChanges 为 false。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_ClearsHasChanges() {
        // Arrange
        var (dict, _) = CreateDurableDict();
        dict.Set(42, 100L);
        dict.HasChanges.Should().BeTrue();

        // Act
        dict.OnCommitSucceeded();

        // Assert
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// OnCommitSucceeded 后状态变为 Clean。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_StateBecomesClean() {
        // Arrange
        var (dict, _) = CreateDurableDict();
        dict.Set(42, 100L);
        dict.State.Should().Be(DurableObjectState.TransientDirty);

        // Act
        dict.OnCommitSucceeded();

        // Assert
        dict.State.Should().Be(DurableObjectState.Clean);
    }

    /// <summary>
    /// OnCommitSucceeded 后值仍可读取。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_ValuesStillAccessible() {
        // Arrange
        var (dict, _) = CreateDurableDict();
        dict.Set(10, 100L);
        dict.Set(20, 200L);

        // Act
        dict.OnCommitSucceeded();

        // Assert - 值从 _committed 读取
        dict[10].Should().Be(100L);
        dict[20].Should().Be(200L);
        dict.Count.Should().Be(2);
    }

    /// <summary>
    /// OnCommitSucceeded 正确合并到 _committed。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_MergesToCommitted() {
        // Arrange
        var (dict, _) = CreateCleanDurableDict(
            (1, (object?)100L),
            (2, (object?)200L)
        );

        // 修改 key=1, 新增 key=3
        dict.Set(1, 999L);
        dict.Set(3, 300L);

        // Act
        dict.OnCommitSucceeded();

        // Assert
        dict[1].Should().Be(999L);  // 覆盖后的值
        dict[2].Should().Be(200L);  // 未修改
        dict[3].Should().Be(300L);  // 新增
        dict.Count.Should().Be(3);
        dict.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// OnCommitSucceeded 正确处理删除。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_HandlesRemove() {
        // Arrange
        var (dict, _) = CreateCleanDurableDict(
            (1, (object?)100L),
            (2, (object?)200L)
        );

        // 删除 key=1
        dict.Remove(1);

        // Act
        dict.OnCommitSucceeded();

        // Assert
        dict.ContainsKey(1).Should().BeFalse();
        dict.ContainsKey(2).Should().BeTrue();
        dict.Count.Should().Be(1);
    }

    /// <summary>
    /// OnCommitSucceeded 后再次修改变为 PersistentDirty。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_ThenModify_BecomesPersistentDirty() {
        // Arrange
        var (dict, _) = CreateDurableDict();
        dict.Set(42, 100L);
        dict.OnCommitSucceeded();
        dict.State.Should().Be(DurableObjectState.Clean);

        // Act
        dict.Set(42, 200L);

        // Assert
        dict.State.Should().Be(DurableObjectState.PersistentDirty);
        dict.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// OnCommitSucceeded Detached 状态抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_Detached_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<long>();

        // Act
        Action act = () => dict.OnCommitSucceeded();

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// 完整的二阶段提交流程测试。
    /// </summary>
    [Fact]
    public void TwoPhaseCommit_RoundTrip() {
        // Arrange
        var (dict, _) = CreateDurableDict();
        dict.Set(10, 100L);
        dict.Set(20, 200L);
        dict.Set(30, 300L);

        // Act - Phase 1: WritePendingDiff
        var buffer = new ArrayBufferWriter<byte>();
        dict.WritePendingDiff(buffer);

        // 验证 Phase 1 不改变状态
        dict.HasChanges.Should().BeTrue();

        // Act - Phase 2: OnCommitSucceeded
        dict.OnCommitSucceeded();

        // Assert
        dict.HasChanges.Should().BeFalse();
        dict.State.Should().Be(DurableObjectState.Clean);

        // 验证序列化内容可以被读取
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(3);

        var values = new Dictionary<ulong, long>();
        while (reader.TryReadNext(out var key, out _, out var valuePayload).Value) {
            values[key] = DiffPayloadReader.ReadVarInt(valuePayload).Value;
        }

        values.Should().BeEquivalentTo(
            new Dictionary<ulong, long>
        {
            { 10, 100L },
            { 20, 200L },
            { 30, 300L }
        }
        );
    }

    #endregion

    #region T-P3-05: DiscardChanges 测试

    // [A-DISCARDCHANGES-REVERT-COMMITTED]: PersistentDirty 对象重置为 Committed State
    // [S-TRANSIENT-DISCARD-DETACH]: TransientDirty 对象变为 Detached

    /// <summary>
    /// PersistentDirty 状态调用 DiscardChanges 后重置为 Committed 状态。
    /// </summary>
    [Fact]
    public void DiscardChanges_PersistentDirty_ResetsToCommitted() {
        // Arrange: 创建有 committed 状态的 dict
        var (dict, _) = CreateCleanDurableDict(
            (1, (object?)100L),
            (2, (object?)200L)
        );

        // Act: 修改后丢弃
        dict.Set(1, 999);   // 覆盖
        dict.Set(3, 300);   // 新增
        dict.Remove(2);     // 删除
        dict.State.Should().Be(DurableObjectState.PersistentDirty);

        dict.DiscardChanges();

        // Assert: 回到 committed 状态
        dict.State.Should().Be(DurableObjectState.Clean);
        dict.HasChanges.Should().BeFalse();
        dict[1].Should().Be(100);                  // 恢复原值
        dict[2].Should().Be(200);                  // 恢复删除的 key
        dict.ContainsKey(3).Should().BeFalse();    // 新增的 key 被丢弃
        dict.Count.Should().Be(2);
    }

    /// <summary>
    /// TransientDirty 状态调用 DiscardChanges 后变为 Detached。
    /// </summary>
    [Fact]
    public void DiscardChanges_TransientDirty_BecomesDetached() {
        // Arrange: 新建 dict（TransientDirty）
        var (dict, _) = CreateDurableDict();
        dict.Set(1, 100);
        dict.State.Should().Be(DurableObjectState.TransientDirty);

        // Act
        dict.DiscardChanges();

        // Assert
        dict.State.Should().Be(DurableObjectState.Detached);

        // 后续访问应抛异常
        Action get = () => _ = dict[1];
        get.Should().Throw<ObjectDetachedException>()
            .Which.ObjectId.Should().BeGreaterThan(0UL);
    }

    /// <summary>
    /// Clean 状态调用 DiscardChanges 是 No-op。
    /// </summary>
    [Fact]
    public void DiscardChanges_Clean_IsNoop() {
        // Arrange
        var (dict, _) = CreateCleanDurableDict((1, (object?)100L));
        dict.State.Should().Be(DurableObjectState.Clean);

        // Act
        dict.DiscardChanges();  // 应该什么都不做

        // Assert
        dict.State.Should().Be(DurableObjectState.Clean);
        dict[1].Should().Be(100);
        dict.Count.Should().Be(1);
    }

    /// <summary>
    /// Detached 状态调用 DiscardChanges 是 no-op（幂等）。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[A-DISCARDCHANGES-REVERT-COMMITTED]</c> — Detached 时为 no-op
    /// </remarks>
    [Fact]
    public void DiscardChanges_Detached_IsNoop() {
        // Arrange: 先 Detach
        var (dict, _) = CreateDurableDict();
        dict.DiscardChanges();  // TransientDirty → Detached
        dict.State.Should().Be(DurableObjectState.Detached);

        // Act: 再次调用 DiscardChanges
        Action discard = () => dict.DiscardChanges();

        // Assert: 不抛异常，保持 Detached 状态（幂等）
        discard.Should().NotThrow();
        dict.State.Should().Be(DurableObjectState.Detached);
    }

    /// <summary>
    /// Detached 后 State 属性仍可读取。
    /// </summary>
    [Fact]
    public void DiscardChanges_AfterDetach_StateCanBeRead() {
        // Arrange
        var (dict, _) = CreateDurableDict();
        dict.DiscardChanges();

        // Act & Assert: State 读取不应抛异常
        dict.State.Should().Be(DurableObjectState.Detached);
    }

    /// <summary>
    /// Detached 后各种语义数据访问都抛异常。
    /// </summary>
    [Fact]
    public void DiscardChanges_AfterDetach_AllAccessThrows() {
        // Arrange
        var (dict, _) = CreateDurableDict();
        dict.Set(1, 100);
        dict.DiscardChanges();

        // Assert: 各种访问都应抛异常
        ((Action)(() => _ = dict[1])).Should().Throw<ObjectDetachedException>();
        ((Action)(() => dict[1] = 200)).Should().Throw<ObjectDetachedException>();
        ((Action)(() => dict.Set(1, 200))).Should().Throw<ObjectDetachedException>();
        ((Action)(() => dict.Remove(1))).Should().Throw<ObjectDetachedException>();
        ((Action)(() => dict.TryGetValue(1, out _))).Should().Throw<ObjectDetachedException>();
        ((Action)(() => dict.ContainsKey(1))).Should().Throw<ObjectDetachedException>();
        ((Action)(() => _ = dict.Count)).Should().Throw<ObjectDetachedException>();
        ((Action)(() => _ = dict.Keys)).Should().Throw<ObjectDetachedException>();
        ((Action)(() => _ = dict.Entries)).Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// 复杂场景：多次修改后 DiscardChanges。
    /// </summary>
    [Fact]
    public void DiscardChanges_ComplexModifications_AllReverted() {
        // Arrange
        var (dict, _) = CreateCleanDurableDict(
            (1, (object?)100L),
            (2, (object?)200L),
            (3, (object?)300L)
        );

        // Act: 多种修改
        dict.Set(1, 111L);       // 覆盖
        dict.Set(2, null);        // 改为 null
        dict.Remove(3);           // 删除
        dict.Set(4, 400L);      // 新增
        dict.Set(5, 500L);      // 新增后删除
        dict.Remove(5);

        dict.DiscardChanges();

        // Assert: 回到 committed 状态
        dict.State.Should().Be(DurableObjectState.Clean);
        dict.HasChanges.Should().BeFalse();
        dict[1].Should().Be(100L);
        dict[2].Should().Be(200L);
        dict[3].Should().Be(300L);
        dict.ContainsKey(4).Should().BeFalse();
        dict.ContainsKey(5).Should().BeFalse();
        dict.Count.Should().Be(3);
    }

    /// <summary>
    /// DiscardChanges 后可再次修改。
    /// </summary>
    [Fact]
    public void DiscardChanges_ThenModify_BecomesPersistentDirtyAgain() {
        // Arrange
        var (dict, _) = CreateCleanDurableDict((1, (object?)100));
        dict.Set(1, 999);
        dict.DiscardChanges();
        dict.State.Should().Be(DurableObjectState.Clean);

        // Act: 再次修改
        dict.Set(1, 888);

        // Assert
        dict.State.Should().Be(DurableObjectState.PersistentDirty);
        dict.HasChanges.Should().BeTrue();
        dict[1].Should().Be(888);
    }

    /// <summary>
    /// DiscardChanges 与 OnCommitSucceeded 的交互。
    /// </summary>
    [Fact]
    public void DiscardChanges_AfterCommit_WorksCorrectly() {
        // Arrange: Commit 后再修改
        var (dict, _) = CreateDurableDict();
        dict.Set(1, 100);
        dict.OnCommitSucceeded();  // 现在 committed[1] = 100

        dict.Set(1, 999);
        dict.Set(2, 200);
        dict.State.Should().Be(DurableObjectState.PersistentDirty);

        // Act
        dict.DiscardChanges();

        // Assert: 回到 commit 后的状态
        dict.State.Should().Be(DurableObjectState.Clean);
        dict[1].Should().Be(100);
        dict.ContainsKey(2).Should().BeFalse();
    }

    #endregion
}
