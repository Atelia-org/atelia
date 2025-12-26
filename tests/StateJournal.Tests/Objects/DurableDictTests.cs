using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DurableDict 基础结构测试。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[A-DURABLEDICT-API-SIGNATURES]</c></item>
///   <item><c>[S-DURABLEDICT-KEY-ULONG-ONLY]</c></item>
///   <item><c>[S-WORKING-STATE-TOMBSTONE-FREE]</c></item>
/// </list>
/// </remarks>
public class DurableDictTests {

    /// <summary>
    /// 辅助方法：将 Dictionary&lt;ulong, T&gt; 转换为 Dictionary&lt;ulong, object?&gt;。
    /// </summary>
    private static Dictionary<ulong, object?> ToObjectDict<T>(Dictionary<ulong, T> source)
        => source.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    #region 构造函数测试

    /// <summary>
    /// 新创建的 DurableDict 处于 TransientDirty 状态。
    /// </summary>
    [Fact]
    public void Constructor_New_SetsTransientDirtyState() {
        // Act
        var dict = new DurableDict(objectId: 42);

        // Assert
        dict.ObjectId.Should().Be(42UL);
        dict.State.Should().Be(DurableObjectState.TransientDirty);
        dict.HasChanges.Should().BeFalse(); // 新建但未修改，_dirtyKeys 为空
        dict.Count.Should().Be(0);
    }

    /// <summary>
    /// 从 Committed State 加载的 DurableDict 处于 Clean 状态。
    /// </summary>
    [Fact]
    public void Constructor_FromCommitted_SetsCleanState() {
        // Arrange
        var committed = new Dictionary<ulong, string?>
        {
            { 1, "one" },
            { 2, "two" },
            { 3, null }
        };

        // Act
        var dict = new DurableDict(objectId: 100, ToObjectDict(committed));

        // Assert
        dict.ObjectId.Should().Be(100UL);
        dict.State.Should().Be(DurableObjectState.Clean);
        dict.HasChanges.Should().BeFalse();
        dict.Count.Should().Be(3);
    }

    /// <summary>
    /// 从 null committed 构造抛出 ArgumentNullException。
    /// </summary>
    [Fact]
    public void Constructor_NullCommitted_ThrowsArgumentNullException() {
        // Act
        Action act = () => new DurableDict(1, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("committed");
    }

    #endregion

    #region Set/Get 基础测试

    /// <summary>
    /// Set 后 Get 返回相同值。
    /// </summary>
    [Fact]
    public void Set_ThenGet_ReturnsSameValue() {
        // Arrange
        var dict = new DurableDict(1);

        // Act
        dict.Set(10, "hello");

        // Assert
        dict.TryGetValue(10, out var value).Should().BeTrue();
        value.Should().Be("hello");
        dict[10].Should().Be("hello");
    }

    /// <summary>
    /// Set null 值可正常存取。
    /// </summary>
    [Fact]
    public void Set_NullValue_CanBeRetrieved() {
        // Arrange
        var dict = new DurableDict(1);

        // Act
        dict.Set(10, null);

        // Assert
        dict.TryGetValue(10, out var value).Should().BeTrue();
        value.Should().BeNull();
        dict.ContainsKey(10).Should().BeTrue();
    }

    /// <summary>
    /// 多次 Set 覆盖之前的值。
    /// </summary>
    [Fact]
    public void Set_MultipleTimes_OverwritesPreviousValue() {
        // Arrange
        var dict = new DurableDict(1);

        // Act
        dict.Set(10, 100);
        dict.Set(10, 200);
        dict.Set(10, 300);

        // Assert
        dict[10].Should().Be(300);
    }

    /// <summary>
    /// 使用索引器设置值。
    /// </summary>
    [Fact]
    public void Indexer_Set_WorksLikeSetMethod() {
        // Arrange
        var dict = new DurableDict(1);

        // Act
        dict[10] = 42;

        // Assert
        dict.TryGetValue(10, out var value).Should().BeTrue();
        value.Should().Be(42);
    }

    /// <summary>
    /// 获取不存在的键抛 KeyNotFoundException。
    /// </summary>
    [Fact]
    public void Indexer_Get_NonExistentKey_ThrowsKeyNotFoundException() {
        // Arrange
        var dict = new DurableDict(1);

        // Act
        Action act = () => _ = dict[999];

        // Assert
        act.Should().Throw<KeyNotFoundException>();
    }

    /// <summary>
    /// TryGetValue 对不存在的键返回 false。
    /// </summary>
    [Fact]
    public void TryGetValue_NonExistentKey_ReturnsFalse() {
        // Arrange
        var dict = new DurableDict(1);

        // Act & Assert
        dict.TryGetValue(999, out var value).Should().BeFalse();
        value.Should().Be(default);
    }

    #endregion

    #region Remove 测试

    /// <summary>
    /// Remove 后 ContainsKey 返回 false。
    /// </summary>
    [Fact]
    public void Remove_ThenContainsKey_ReturnsFalse() {
        // Arrange
        var dict = new DurableDict(1);
        dict.Set(10, "hello");

        // Act
        var removed = dict.Remove(10);

        // Assert
        removed.Should().BeTrue();
        dict.ContainsKey(10).Should().BeFalse();
        dict.TryGetValue(10, out _).Should().BeFalse();
    }

    /// <summary>
    /// Remove 不存在的键返回 false。
    /// </summary>
    [Fact]
    public void Remove_NonExistentKey_ReturnsFalse() {
        // Arrange
        var dict = new DurableDict(1);

        // Act
        var removed = dict.Remove(999);

        // Assert
        removed.Should().BeFalse();
    }

    /// <summary>
    /// Remove 只存在于 _committed 的键返回 true。
    /// </summary>
    [Fact]
    public void Remove_KeyOnlyInCommitted_ReturnsTrue() {
        // Arrange
        var committed = new Dictionary<ulong, int> { { 10, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));

        // Act
        var removed = dict.Remove(10);

        // Assert
        removed.Should().BeTrue();
        dict.ContainsKey(10).Should().BeFalse();
    }

    /// <summary>
    /// [S-WORKING-STATE-TOMBSTONE-FREE] Remove 后枚举不含该 key。
    /// </summary>
    [Fact]
    public void Remove_KeyNotInEnumeration() {
        // Arrange
        var dict = new DurableDict(1);
        dict.Set(1, 100);
        dict.Set(2, 200);
        dict.Set(3, 300);

        // Act
        dict.Remove(2);

        // Assert
        dict.Keys.Should().BeEquivalentTo(new ulong[] { 1, 3 });
        dict.Entries.Select(e => e.Key).Should().BeEquivalentTo(new ulong[] { 1, 3 });
    }

    #endregion

    #region Working State 查询优先测试

    /// <summary>
    /// Set 后立即可读（Working State 优先）。
    /// </summary>
    [Fact]
    public void WorkingState_TakesPrecedence_OverCommitted() {
        // Arrange
        var committed = new Dictionary<ulong, int> { { 10, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));

        // Act
        dict.Set(10, 999);

        // Assert - _working 的值覆盖 _committed
        dict[10].Should().Be(999);
    }

    /// <summary>
    /// 未修改的键从 _committed 读取。
    /// </summary>
    [Fact]
    public void UnmodifiedKey_ReadsFromCommitted() {
        // Arrange
        var committed = new Dictionary<ulong, string?>
        {
            { 1, "one" },
            { 2, "two" }
        };
        var dict = new DurableDict(1, ToObjectDict(committed));

        // Act - 只修改 key=1
        dict.Set(1, "ONE");

        // Assert
        dict[1].Should().Be("ONE");  // 从 _working
        dict[2].Should().Be("two");  // 从 _committed
    }

    /// <summary>
    /// Working 新增的键被正确枚举。
    /// </summary>
    [Fact]
    public void NewKeyInWorking_IncludedInEnumeration() {
        // Arrange
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));

        // Act
        dict.Set(2, 200);
        dict.Set(3, 300);

        // Assert
        dict.Count.Should().Be(3);
        dict.Keys.Should().BeEquivalentTo(new ulong[] { 1, 2, 3 });
    }

    #endregion

    #region 状态转换测试

    /// <summary>
    /// Clean 状态修改后变为 PersistentDirty。
    /// </summary>
    [Fact]
    public void Clean_AfterSet_BecomesPersistentDirty() {
        // Arrange
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));
        dict.State.Should().Be(DurableObjectState.Clean);

        // Act
        dict.Set(1, 999);

        // Assert
        dict.State.Should().Be(DurableObjectState.PersistentDirty);
        dict.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// TransientDirty 状态修改后保持 TransientDirty。
    /// </summary>
    [Fact]
    public void TransientDirty_AfterSet_RemainsTransientDirty() {
        // Arrange
        var dict = new DurableDict(1);
        dict.State.Should().Be(DurableObjectState.TransientDirty);

        // Act
        dict.Set(1, 100);

        // Assert
        dict.State.Should().Be(DurableObjectState.TransientDirty);
        dict.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// HasChanges 反映 _dirtyKeys 状态。
    /// </summary>
    [Fact]
    public void HasChanges_ReflectsDirtyKeys() {
        // Arrange
        var dict = new DurableDict(1);
        dict.HasChanges.Should().BeFalse();

        // Act & Assert
        dict.Set(1, 100);
        dict.HasChanges.Should().BeTrue();

        dict.Set(2, 200);
        dict.HasChanges.Should().BeTrue();
    }

    /// <summary>
    /// Clean 状态 Remove 后变为 PersistentDirty。
    /// </summary>
    [Fact]
    public void Clean_AfterRemove_BecomesPersistentDirty() {
        // Arrange
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));

        // Act
        dict.Remove(1);

        // Assert
        dict.State.Should().Be(DurableObjectState.PersistentDirty);
        dict.HasChanges.Should().BeTrue();
    }

    #endregion

    #region Detached 状态测试

    /// <summary>
    /// 创建一个 Detached 状态的 DurableDict（通过反射设置状态）。
    /// </summary>
    private static DurableDict CreateDetachedDict<TValue>(ulong objectId) {
        var dict = new DurableDict(objectId);
        // 使用反射设置 _state 为 Detached
        var stateField = typeof(DurableDict).GetField(
            "_state",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        stateField!.SetValue(dict, DurableObjectState.Detached);
        return dict;
    }

    /// <summary>
    /// Detached 状态下 TryGetValue 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_TryGetValue_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => dict.TryGetValue(1, out _);

        // Assert
        act.Should().Throw<ObjectDetachedException>()
            .Which.ObjectId.Should().Be(42UL);
    }

    /// <summary>
    /// Detached 状态下 ContainsKey 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_ContainsKey_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => dict.ContainsKey(1);

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Indexer Get 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_IndexerGet_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => _ = dict[1];

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Indexer Set 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_IndexerSet_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => dict[1] = 100;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Set 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Set_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => dict.Set(1, 100);

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Remove 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Remove_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => dict.Remove(1);

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Count 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Count_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => _ = dict.Count;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Keys 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Keys_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => _ = dict.Keys;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 Entries 抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void Detached_Entries_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => _ = dict.Entries;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    /// <summary>
    /// Detached 状态下 State 属性仍可读取（不抛异常）。
    /// </summary>
    [Fact]
    public void Detached_State_CanBeRead() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act & Assert - State 读取不应抛异常
        dict.State.Should().Be(DurableObjectState.Detached);
    }

    /// <summary>
    /// Detached 状态下 HasChanges 抛 ObjectDetachedException。
    /// </summary>
    /// <remarks>
    /// 对应条款：[S-DETACHED-ACCESS-TIERING] - HasChanges 是语义成员，Detached 时 MUST throw。
    /// 畅谈会 #3 决议：规则优先级 R2 (Fail-Fast) &gt; R1 (属性惯例)。
    /// </remarks>
    [Fact]
    public void Detached_HasChanges_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<int>(42);

        // Act
        Action act = () => _ = dict.HasChanges;

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    #endregion

    #region WritePendingDiff 测试

    /// <summary>
    /// WritePendingDiff 正确序列化单个值。
    /// </summary>
    [Fact]
    public void WritePendingDiff_SingleValue_SerializesCorrectly() {
        // Arrange
        var dict = new DurableDict(1);
        dict.Set(42, 100L);
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        dict.WritePendingDiff(buffer);

        // Assert - 使用 DiffPayloadReader 验证
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(1);
        reader.HasError.Should().BeFalse();

        var result = reader.TryReadNext(out var key, out var valueType, out var valuePayload);
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
        key.Should().Be(42UL);
        valueType.Should().Be(ValueType.VarInt);

        var varIntResult = DiffPayloadReader.ReadVarInt(valuePayload);
        varIntResult.IsSuccess.Should().BeTrue();
        varIntResult.Value.Should().Be(100L);
    }

    /// <summary>
    /// WritePendingDiff 正确序列化多个值（验证 key 升序）。
    /// </summary>
    [Fact]
    public void WritePendingDiff_MultipleValues_KeysAreSorted() {
        // Arrange
        var dict = new DurableDict(1);
        // 以乱序设置值
        dict.Set(100, 1000L);
        dict.Set(10, 100L);
        dict.Set(50, 500L);
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        dict.WritePendingDiff(buffer);

        // Assert - 验证 key 升序
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(3);

        var keys = new List<ulong>();
        while (reader.TryReadNext(out var key, out _, out _).Value) {
            keys.Add(key);
        }

        keys.Should().BeEquivalentTo(new ulong[] { 10, 50, 100 });
        keys.Should().BeInAscendingOrder();
    }

    /// <summary>
    /// WritePendingDiff 正确序列化 null 值。
    /// </summary>
    [Fact]
    public void WritePendingDiff_NullValue_WritesNull() {
        // Arrange
        var dict = new DurableDict(1);
        dict.Set(42, null);
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        dict.WritePendingDiff(buffer);

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out var key, out var valueType, out _);
        result.IsSuccess.Should().BeTrue();
        key.Should().Be(42UL);
        valueType.Should().Be(ValueType.Null);
    }

    /// <summary>
    /// WritePendingDiff 删除操作序列化为 Tombstone。
    /// </summary>
    [Fact]
    public void WritePendingDiff_Remove_WritesTombstone() {
        // Arrange
        var committed = new Dictionary<ulong, long> { { 10, 100L } };
        var dict = new DurableDict(1, ToObjectDict(committed));
        dict.Remove(10);
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        dict.WritePendingDiff(buffer);

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out var key, out var valueType, out _);
        result.IsSuccess.Should().BeTrue();
        key.Should().Be(10UL);
        valueType.Should().Be(ValueType.Tombstone);
    }

    /// <summary>
    /// WritePendingDiff 后 HasChanges 仍为 true（[S-POSTCOMMIT-WRITE-ISOLATION]）。
    /// </summary>
    [Fact]
    public void WritePendingDiff_DoesNotUpdateState() {
        // Arrange
        var dict = new DurableDict(1);
        dict.Set(42, 100L);
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        dict.WritePendingDiff(buffer);

        // Assert - 状态不变
        dict.HasChanges.Should().BeTrue();
        dict.State.Should().Be(DurableObjectState.TransientDirty);
    }

    /// <summary>
    /// WritePendingDiff 空变更输出 PairCount=0。
    /// </summary>
    [Fact]
    public void WritePendingDiff_NoChanges_WritesEmptyPayload() {
        // Arrange
        var committed = new Dictionary<ulong, long> { { 10, 100L } };
        var dict = new DurableDict(1, ToObjectDict(committed));
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        dict.WritePendingDiff(buffer);

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(0);
    }

    /// <summary>
    /// WritePendingDiff 支持 int 类型（自动转为 VarInt）。
    /// </summary>
    [Fact]
    public void WritePendingDiff_IntValue_SerializesAsVarInt() {
        // Arrange
        var dict = new DurableDict(1);
        dict.Set(42, 999);
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        dict.WritePendingDiff(buffer);

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(1);

        var result = reader.TryReadNext(out _, out var valueType, out var valuePayload);
        result.IsSuccess.Should().BeTrue();
        valueType.Should().Be(ValueType.VarInt);

        var varIntResult = DiffPayloadReader.ReadVarInt(valuePayload);
        varIntResult.IsSuccess.Should().BeTrue();
        varIntResult.Value.Should().Be(999L);
    }

    /// <summary>
    /// WritePendingDiff 混合 Set/Remove 操作。
    /// </summary>
    [Fact]
    public void WritePendingDiff_MixedOperations_SerializesCorrectly() {
        // Arrange
        var committed = new Dictionary<ulong, long>
        {
            { 1, 100L },
            { 2, 200L },
            { 3, 300L }
        };
        var dict = new DurableDict(1, ToObjectDict(committed));

        // 覆盖 key=1, 删除 key=2, 新增 key=4
        dict.Set(1, 999L);
        dict.Remove(2);
        dict.Set(4, 400L);

        var buffer = new ArrayBufferWriter<byte>();

        // Act
        dict.WritePendingDiff(buffer);

        // Assert
        var reader = new DiffPayloadReader(buffer.WrittenSpan);
        reader.PairCount.Should().Be(3);

        var pairs = new List<(ulong Key, ValueType Type, long? Value)>();
        while (reader.TryReadNext(out var key, out var valueType, out var valuePayload).Value) {
            long? val = null;
            if (valueType == ValueType.VarInt) {
                val = DiffPayloadReader.ReadVarInt(valuePayload).Value;
            }
            pairs.Add((key, valueType, val));
        }

        pairs.Should().HaveCount(3);
        pairs[0].Should().Be((1UL, ValueType.VarInt, 999L));   // 覆盖
        pairs[1].Should().Be((2UL, ValueType.Tombstone, null)); // 删除
        pairs[2].Should().Be((4UL, ValueType.VarInt, 400L));   // 新增
    }

    /// <summary>
    /// WritePendingDiff 不支持的类型抛 NotSupportedException。
    /// </summary>
    [Fact]
    public void WritePendingDiff_UnsupportedType_ThrowsNotSupportedException() {
        // Arrange
        var dict = new DurableDict(1);
        dict.Set(42, "hello");
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        Action act = () => dict.WritePendingDiff(buffer);

        // Assert
        act.Should().Throw<NotSupportedException>()
            .WithMessage("*Unsupported value type*");
    }

    /// <summary>
    /// WritePendingDiff Detached 状态抛 ObjectDetachedException。
    /// </summary>
    [Fact]
    public void WritePendingDiff_Detached_ThrowsObjectDetachedException() {
        // Arrange
        var dict = CreateDetachedDict<long>(42);
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        Action act = () => dict.WritePendingDiff(buffer);

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }

    #endregion

    #region OnCommitSucceeded 测试

    /// <summary>
    /// OnCommitSucceeded 后 HasChanges 为 false。
    /// </summary>
    [Fact]
    public void OnCommitSucceeded_ClearsHasChanges() {
        // Arrange
        var dict = new DurableDict(1);
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
        var dict = new DurableDict(1);
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
        var dict = new DurableDict(1);
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
        var committed = new Dictionary<ulong, long>
        {
            { 1, 100L },
            { 2, 200L }
        };
        var dict = new DurableDict(1, ToObjectDict(committed));

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
        var committed = new Dictionary<ulong, long>
        {
            { 1, 100L },
            { 2, 200L }
        };
        var dict = new DurableDict(1, ToObjectDict(committed));

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
        var dict = new DurableDict(1);
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
        var dict = CreateDetachedDict<long>(42);

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
        var dict = new DurableDict(1);
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
        var committed = new Dictionary<ulong, long?>
        {
            { 1, 100 },
            { 2, 200 }
        };
        var dict = new DurableDict(1, ToObjectDict(committed));

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
        var dict = new DurableDict(42);
        dict.Set(1, 100);
        dict.State.Should().Be(DurableObjectState.TransientDirty);

        // Act
        dict.DiscardChanges();

        // Assert
        dict.State.Should().Be(DurableObjectState.Detached);

        // 后续访问应抛异常
        Action get = () => _ = dict[1];
        get.Should().Throw<ObjectDetachedException>()
            .Which.ObjectId.Should().Be(42UL);
    }

    /// <summary>
    /// Clean 状态调用 DiscardChanges 是 No-op。
    /// </summary>
    [Fact]
    public void DiscardChanges_Clean_IsNoop() {
        // Arrange
        var committed = new Dictionary<ulong, long?> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));
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
        var dict = new DurableDict(42);
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
        var dict = new DurableDict(1);
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
        var dict = new DurableDict(42);
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
        var committed = new Dictionary<ulong, string?>
        {
            { 1, "one" },
            { 2, "two" },
            { 3, "three" }
        };
        var dict = new DurableDict(1, ToObjectDict(committed));

        // Act: 多种修改
        dict.Set(1, "ONE");       // 覆盖
        dict.Set(2, null);        // 改为 null
        dict.Remove(3);           // 删除
        dict.Set(4, "four");      // 新增
        dict.Set(5, "five");      // 新增后删除
        dict.Remove(5);

        dict.DiscardChanges();

        // Assert: 回到 committed 状态
        dict.State.Should().Be(DurableObjectState.Clean);
        dict.HasChanges.Should().BeFalse();
        dict[1].Should().Be("one");
        dict[2].Should().Be("two");
        dict[3].Should().Be("three");
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
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));
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
        var dict = new DurableDict(1);
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

    #region 复杂场景测试

    /// <summary>
    /// 混合 Set/Remove 操作。
    /// </summary>
    [Fact]
    public void MixedOperations_SetAndRemove() {
        // Arrange
        var committed = new Dictionary<ulong, int>
        {
            { 1, 100 },
            { 2, 200 },
            { 3, 300 }
        };
        var dict = new DurableDict(1, ToObjectDict(committed));

        // Act
        dict.Set(1, 999);      // 覆盖
        dict.Remove(2);        // 删除 _committed 中的
        dict.Set(4, 400);      // 新增
        dict.Set(5, 500);      // 新增
        dict.Remove(5);        // 删除刚新增的

        // Assert
        dict.Count.Should().Be(3); // 1, 3, 4
        dict.Keys.Should().BeEquivalentTo(new ulong[] { 1, 3, 4 });
        dict[1].Should().Be(999);
        dict[3].Should().Be(300);
        dict[4].Should().Be(400);
        dict.ContainsKey(2).Should().BeFalse();
        dict.ContainsKey(5).Should().BeFalse();
    }

    /// <summary>
    /// Entries 枚举正确合并 _committed 和 _working。
    /// </summary>
    [Fact]
    public void Entries_MergesCommittedAndWorking() {
        // Arrange
        var committed = new Dictionary<ulong, string?>
        {
            { 1, "one" },
            { 2, "two" }
        };
        var dict = new DurableDict(1, ToObjectDict(committed));

        // Act
        dict.Set(1, "ONE");    // 覆盖
        dict.Set(3, "three");  // 新增

        // Assert
        var entries = dict.Entries.ToDictionary(e => e.Key, e => e.Value);
        entries.Should().HaveCount(3);
        entries[1].Should().Be("ONE");
        entries[2].Should().Be("two");
        entries[3].Should().Be("three");
    }

    /// <summary>
    /// 空 Committed + 空 Working = 空字典。
    /// </summary>
    [Fact]
    public void EmptyDict_CountIsZero() {
        // Arrange
        var dict = new DurableDict(1);

        // Assert
        dict.Count.Should().Be(0);
        dict.Keys.Should().BeEmpty();
        dict.Entries.Should().BeEmpty();
    }

    /// <summary>
    /// 值类型测试（int）。
    /// </summary>
    [Fact]
    public void ValueType_Int_Works() {
        var dict = new DurableDict(1);
        dict.Set(1, 42);
        dict[1].Should().Be(42);
    }

    /// <summary>
    /// 引用类型测试（object）。
    /// </summary>
    [Fact]
    public void ReferenceType_Object_Works() {
        var dict = new DurableDict(1);
        var obj = new object();
        dict.Set(1, obj);
        dict[1].Should().BeSameAs(obj);
    }

    /// <summary>
    /// 可空值类型测试（int?）。
    /// </summary>
    [Fact]
    public void NullableValueType_Works() {
        var dict = new DurableDict(1);
        dict.Set(1, 42);
        dict.Set(2, null);

        dict[1].Should().Be(42);
        dict[2].Should().BeNull();
        dict.ContainsKey(2).Should().BeTrue();
    }

    #endregion

    #region ObjectDetachedException 测试

    /// <summary>
    /// ObjectDetachedException 包含正确的 ObjectId。
    /// </summary>
    [Fact]
    public void ObjectDetachedException_ContainsObjectId() {
        // Arrange & Act
        var ex = new ObjectDetachedException(12345UL);

        // Assert
        ex.ObjectId.Should().Be(12345UL);
        ex.Message.Should().Contain("12345");
        ex.Message.Should().Contain("detached");
    }

    #endregion

    #region T-P3-04: _dirtyKeys 精确追踪测试

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
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));
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
        var dict = new DurableDict(1);
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
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));

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
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));

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
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));
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
        var dict = new DurableDict(1);
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
        var committed = new Dictionary<ulong, int>
        {
            { 1, 100 },
            { 2, 200 }
        };
        var dict = new DurableDict(1, ToObjectDict(committed));

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
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));
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
        var committed = new Dictionary<ulong, string?> { { 1, null } };
        var dict = new DurableDict(1, ToObjectDict(committed));
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
        var dict = new DurableDict(1);
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
        var committed = new Dictionary<ulong, int> { { 1, 100 } };
        var dict = new DurableDict(1, ToObjectDict(committed));

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
        var committed = new Dictionary<ulong, long> { { 1, 100L } };
        var dict = new DurableDict(1, ToObjectDict(committed));

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

    #endregion
}
