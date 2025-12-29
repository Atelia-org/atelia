// Source: Atelia.StateJournal.Tests - IDurableObject 接口测试
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §3.1

using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Core;

/// <summary>
/// IDurableObject 接口契约测试。
/// </summary>
/// <remarks>
/// <para>
/// 使用 <see cref="FakeDurableObject"/> 作为 test double 验证接口契约。
/// </para>
/// <para>
/// 对应条款：
/// <list type="bullet">
///   <item><description><c>[A-OBJECT-STATE-PROPERTY]</c>: State 属性复杂度 O(1)，不抛异常</description></item>
///   <item><description><c>[A-HASCHANGES-O1-COMPLEXITY]</c>: HasChanges 属性复杂度 O(1)</description></item>
///   <item><description><c>[S-TRANSIENT-DISCARD-DETACH]</c>: TransientDirty → DiscardChanges → Detached</description></item>
/// </list>
/// </para>
/// </remarks>
public class IDurableObjectTests {
    // ========================================================================
    // State Property Tests - [A-OBJECT-STATE-PROPERTY]
    // ========================================================================

    /// <summary>
    /// 测试 State 属性在 Clean 状态下读取不抛异常。
    /// </summary>
    [Fact]
    public void State_WhenClean_DoesNotThrow() {
        var obj = FakeDurableObject.CreateClean(objectId: 1);

        var act = () => obj.State;

        act.Should().NotThrow();
        obj.State.Should().Be(DurableObjectState.Clean);
    }

    /// <summary>
    /// 测试 State 属性在 PersistentDirty 状态下读取不抛异常。
    /// </summary>
    [Fact]
    public void State_WhenPersistentDirty_DoesNotThrow() {
        var obj = FakeDurableObject.CreatePersistentDirty(objectId: 2);

        var act = () => obj.State;

        act.Should().NotThrow();
        obj.State.Should().Be(DurableObjectState.PersistentDirty);
    }

    /// <summary>
    /// 测试 State 属性在 TransientDirty 状态下读取不抛异常。
    /// </summary>
    [Fact]
    public void State_WhenTransientDirty_DoesNotThrow() {
        var obj = FakeDurableObject.CreateTransientDirty(objectId: 3);

        var act = () => obj.State;

        act.Should().NotThrow();
        obj.State.Should().Be(DurableObjectState.TransientDirty);
    }

    /// <summary>
    /// 测试 State 属性在 Detached 状态下读取不抛异常。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[A-OBJECT-STATE-PROPERTY]</c> - 读取 MUST NOT 抛异常（含 Detached 状态）
    /// </remarks>
    [Fact]
    public void State_WhenDetached_DoesNotThrow() {
        var obj = FakeDurableObject.CreateDetached(objectId: 4);

        var act = () => obj.State;

        act.Should().NotThrow();
        obj.State.Should().Be(DurableObjectState.Detached);
    }

    // ========================================================================
    // HasChanges Property Tests - [A-HASCHANGES-O1-COMPLEXITY]
    // ========================================================================

    /// <summary>
    /// 测试 HasChanges 在 Clean 状态下为 false。
    /// </summary>
    [Fact]
    public void HasChanges_WhenClean_ReturnsFalse() {
        var obj = FakeDurableObject.CreateClean(objectId: 10);

        obj.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 测试 HasChanges 在 PersistentDirty 状态下为 true。
    /// </summary>
    [Fact]
    public void HasChanges_WhenPersistentDirty_ReturnsTrue() {
        var obj = FakeDurableObject.CreatePersistentDirty(objectId: 11);

        obj.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// 测试 HasChanges 在 TransientDirty 状态下为 true。
    /// </summary>
    [Fact]
    public void HasChanges_WhenTransientDirty_ReturnsTrue() {
        var obj = FakeDurableObject.CreateTransientDirty(objectId: 12);

        obj.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// 测试 HasChanges 在 Detached 状态下为 false。
    /// </summary>
    [Fact]
    public void HasChanges_WhenDetached_ReturnsFalse() {
        var obj = FakeDurableObject.CreateDetached(objectId: 13);

        obj.HasChanges.Should().BeFalse();
    }

    // ========================================================================
    // State-HasChanges Consistency Tests
    // ========================================================================

    /// <summary>
    /// 测试 HasChanges 与 State 的一致性：
    /// HasChanges == true 当且仅当 State 为 PersistentDirty 或 TransientDirty。
    /// </summary>
    [Theory]
    [InlineData(DurableObjectState.Clean, false)]
    [InlineData(DurableObjectState.PersistentDirty, true)]
    [InlineData(DurableObjectState.TransientDirty, true)]
    [InlineData(DurableObjectState.Detached, false)]
    public void HasChanges_IsConsistentWithState(DurableObjectState state, bool expectedHasChanges) {
        var obj = FakeDurableObject.CreateWithState(objectId: 100, state);

        obj.HasChanges.Should().Be(expectedHasChanges);
    }

    // ========================================================================
    // State Transition Tests - DiscardChanges
    // ========================================================================

    /// <summary>
    /// 测试 PersistentDirty 对象调用 DiscardChanges 后变为 Clean。
    /// </summary>
    [Fact]
    public void DiscardChanges_WhenPersistentDirty_BecomesClean() {
        var obj = FakeDurableObject.CreatePersistentDirty(objectId: 20);

        obj.DiscardChanges();

        obj.State.Should().Be(DurableObjectState.Clean);
        obj.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 测试 TransientDirty 对象调用 DiscardChanges 后变为 Detached。
    /// </summary>
    /// <remarks>
    /// 对应条款：<c>[S-TRANSIENT-DISCARD-DETACH]</c>
    /// </remarks>
    [Fact]
    public void DiscardChanges_WhenTransientDirty_BecomesDetached() {
        var obj = FakeDurableObject.CreateTransientDirty(objectId: 21);

        obj.DiscardChanges();

        obj.State.Should().Be(DurableObjectState.Detached);
        obj.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 测试 Clean 对象调用 DiscardChanges 保持 Clean（No-op）。
    /// </summary>
    [Fact]
    public void DiscardChanges_WhenClean_RemainsClean() {
        var obj = FakeDurableObject.CreateClean(objectId: 22);

        obj.DiscardChanges();

        obj.State.Should().Be(DurableObjectState.Clean);
    }

    /// <summary>
    /// 测试 Detached 对象调用 DiscardChanges 保持 Detached（No-op）。
    /// </summary>
    [Fact]
    public void DiscardChanges_WhenDetached_RemainsDetached() {
        var obj = FakeDurableObject.CreateDetached(objectId: 23);

        obj.DiscardChanges();

        obj.State.Should().Be(DurableObjectState.Detached);
    }

    // ========================================================================
    // State Transition Tests - OnCommitSucceeded
    // ========================================================================

    /// <summary>
    /// 测试 PersistentDirty 对象调用 OnCommitSucceeded 后变为 Clean。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_WhenPersistentDirty_BecomesClean() {
        var obj = FakeDurableObject.CreatePersistentDirty(objectId: 30);

        obj.OnCommitSucceeded();

        obj.State.Should().Be(DurableObjectState.Clean);
        obj.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 测试 TransientDirty 对象调用 OnCommitSucceeded 后变为 Clean。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_WhenTransientDirty_BecomesClean() {
        var obj = FakeDurableObject.CreateTransientDirty(objectId: 31);

        obj.OnCommitSucceeded();

        obj.State.Should().Be(DurableObjectState.Clean);
        obj.HasChanges.Should().BeFalse();
    }

    // ========================================================================
    // State Transition Tests - Full Lifecycle
    // ========================================================================

    /// <summary>
    /// 测试完整生命周期：Clean → PersistentDirty → Clean（通过 Commit）。
    /// </summary>
    [Fact]
    public void FullLifecycle_Clean_ToPersistentDirty_ToClean_ViaCommit() {
        var obj = FakeDurableObject.CreateClean(objectId: 40);
        obj.State.Should().Be(DurableObjectState.Clean);
        obj.HasChanges.Should().BeFalse();

        // Simulate modification
        obj.SimulateModification();
        obj.State.Should().Be(DurableObjectState.PersistentDirty);
        obj.HasChanges.Should().BeTrue();

        // Commit
        obj.OnCommitSucceeded();
        obj.State.Should().Be(DurableObjectState.Clean);
        obj.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 测试完整生命周期：Clean → PersistentDirty → Clean（通过 Discard）。
    /// </summary>
    [Fact]
    public void FullLifecycle_Clean_ToPersistentDirty_ToClean_ViaDiscard() {
        var obj = FakeDurableObject.CreateClean(objectId: 41);

        // Simulate modification
        obj.SimulateModification();
        obj.State.Should().Be(DurableObjectState.PersistentDirty);

        // Discard
        obj.DiscardChanges();
        obj.State.Should().Be(DurableObjectState.Clean);
        obj.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 测试完整生命周期：TransientDirty → Clean（通过 Commit）。
    /// </summary>
    [Fact]
    public void FullLifecycle_TransientDirty_ToClean_ViaCommit() {
        var obj = FakeDurableObject.CreateTransientDirty(objectId: 42);
        obj.State.Should().Be(DurableObjectState.TransientDirty);
        obj.HasChanges.Should().BeTrue();

        // Commit
        obj.OnCommitSucceeded();
        obj.State.Should().Be(DurableObjectState.Clean);
        obj.HasChanges.Should().BeFalse();
    }

    /// <summary>
    /// 测试完整生命周期：TransientDirty → Detached（通过 Discard）。
    /// </summary>
    [Fact]
    public void FullLifecycle_TransientDirty_ToDetached_ViaDiscard() {
        var obj = FakeDurableObject.CreateTransientDirty(objectId: 43);
        obj.State.Should().Be(DurableObjectState.TransientDirty);
        obj.HasChanges.Should().BeTrue();

        // Discard
        obj.DiscardChanges();
        obj.State.Should().Be(DurableObjectState.Detached);
        obj.HasChanges.Should().BeFalse();
    }

    // ========================================================================
    // WritePendingDiff Tests
    // ========================================================================

    /// <summary>
    /// 测试 WritePendingDiff 不修改状态（Prepare 阶段不改变内存状态）。
    /// </summary>
    [Fact]
    public void WritePendingDiff_DoesNotChangeState() {
        var obj = FakeDurableObject.CreatePersistentDirty(objectId: 50);
        var writer = new ArrayBufferWriter<byte>();

        obj.WritePendingDiff(writer);

        // State should still be PersistentDirty
        obj.State.Should().Be(DurableObjectState.PersistentDirty);
        obj.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// 测试 WritePendingDiff 写入数据到 buffer。
    /// </summary>
    [Fact]
    public void WritePendingDiff_WritesToBuffer() {
        var obj = FakeDurableObject.CreatePersistentDirty(objectId: 51);
        var writer = new ArrayBufferWriter<byte>();

        obj.WritePendingDiff(writer);

        // FakeDurableObject writes a marker
        writer.WrittenCount.Should().BeGreaterThan(0);
    }

    // ========================================================================
    // ObjectId Tests
    // ========================================================================

    /// <summary>
    /// 测试 ObjectId 属性正确返回。
    /// </summary>
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    public void ObjectId_ReturnsCorrectValue(ulong expectedId) {
        var obj = FakeDurableObject.CreateClean(objectId: expectedId);

        obj.ObjectId.Should().Be(expectedId);
    }
}

// ============================================================================
// Test Double: FakeDurableObject
// ============================================================================

/// <summary>
/// IDurableObject 的测试 double 实现。
/// </summary>
/// <remarks>
/// <para>
/// 此类用于验证 IDurableObject 接口契约。
/// 实现了正确的状态转换逻辑。
/// </para>
/// </remarks>
internal sealed class FakeDurableObject : IDurableObject {
    private DurableObjectState _state;
    private bool _wasTransient; // 记录是否曾是 Transient 状态

    /// <summary>
    /// 对象的唯一标识符。
    /// </summary>
    public ulong ObjectId { get; }

    /// <summary>
    /// 对象的生命周期状态。
    /// </summary>
    /// <remarks>
    /// 复杂度 O(1)，不抛异常。
    /// </remarks>
    public DurableObjectState State => _state;

    /// <summary>
    /// 是否有未提交的变更。
    /// </summary>
    /// <remarks>
    /// 复杂度 O(1)。
    /// </remarks>
    public bool HasChanges => _state is DurableObjectState.PersistentDirty or DurableObjectState.TransientDirty;

    private FakeDurableObject(ulong objectId, DurableObjectState initialState) {
        ObjectId = objectId;
        _state = initialState;
        _wasTransient = initialState == DurableObjectState.TransientDirty;
    }

    // ========================================================================
    // Factory Methods
    // ========================================================================

    /// <summary>
    /// 创建 Clean 状态的对象（模拟 LoadObject 返回）。
    /// </summary>
    public static FakeDurableObject CreateClean(ulong objectId)
        => new(objectId, DurableObjectState.Clean);

    /// <summary>
    /// 创建 PersistentDirty 状态的对象。
    /// </summary>
    public static FakeDurableObject CreatePersistentDirty(ulong objectId)
        => new(objectId, DurableObjectState.PersistentDirty);

    /// <summary>
    /// 创建 TransientDirty 状态的对象（模拟 CreateObject 返回）。
    /// </summary>
    public static FakeDurableObject CreateTransientDirty(ulong objectId)
        => new(objectId, DurableObjectState.TransientDirty);

    /// <summary>
    /// 创建 Detached 状态的对象。
    /// </summary>
    public static FakeDurableObject CreateDetached(ulong objectId)
        => new(objectId, DurableObjectState.Detached);

    /// <summary>
    /// 创建指定状态的对象。
    /// </summary>
    public static FakeDurableObject CreateWithState(ulong objectId, DurableObjectState state)
        => new(objectId, state);

    // ========================================================================
    // IDurableObject Implementation
    // ========================================================================

    /// <inheritdoc/>
    public void WritePendingDiff(IBufferWriter<byte> writer) {
        // 写入一个简单的标记以验证方法被调用
        // 真实实现会序列化 diff payload
        var span = writer.GetSpan(4);
        span[0] = 0xDE;
        span[1] = 0xAD;
        span[2] = 0xBE;
        span[3] = 0xEF;
        writer.Advance(4);
    }

    /// <inheritdoc/>
    public void OnCommitSucceeded() {
        // Commit 成功后状态变为 Clean
        _state = DurableObjectState.Clean;
        _wasTransient = false;
    }

    /// <inheritdoc/>
    public void DiscardChanges() {
        _state = _state switch {
            DurableObjectState.PersistentDirty => DurableObjectState.Clean,
            DurableObjectState.TransientDirty => DurableObjectState.Detached,
            // Clean 和 Detached 是 No-op
            _ => _state
        };
    }

    // ========================================================================
    // Test Helpers
    // ========================================================================

    /// <summary>
    /// 模拟对象被修改（用于测试状态转换）。
    /// </summary>
    /// <remarks>
    /// 只能从 Clean 状态转换到 PersistentDirty。
    /// </remarks>
    public void SimulateModification() {
        if (_state == DurableObjectState.Clean) {
            _state = DurableObjectState.PersistentDirty;
        }
    }
}
