using System.Buffers;
using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using static Atelia.StateJournal.Tests.TestHelper;

namespace Atelia.StateJournal.Tests.Objects;

/// <summary>
/// DurableDict 序列化测试（WritePendingDiff）。
/// </summary>
/// <remarks>
/// 对应条款：
/// <list type="bullet">
///   <item><c>[A-DURABLEDICT-API-SIGNATURES]</c></item>
///   <item><c>[S-DURABLEDICT-KEY-ULONG-ONLY]</c></item>
///   <item><c>[S-WORKING-STATE-TOMBSTONE-FREE]</c></item>
/// </list>
/// </remarks>
public class DurableDictSerializationTests {

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

    /// <summary>
    /// WritePendingDiff 正确序列化单个值。
    /// </summary>
    [Fact]
    public void WritePendingDiff_SingleValue_SerializesCorrectly() {
        // Arrange
        var (dict, ws) = CreateDurableDict();
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
        var (dict, ws) = CreateDurableDict();
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
        var (dict, _) = CreateDurableDict();
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
        var (dict, _) = CreateCleanDurableDict((10, (object?)100L));
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
        var (dict, _) = CreateDurableDict();
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
        var (dict, _) = CreateCleanDurableDict((10, (object?)100L));
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
        var (dict, _) = CreateDurableDict();
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
        var (dict, _) = CreateCleanDurableDict(
            (1, (object?)100L),
            (2, (object?)200L),
            (3, (object?)300L)
        );

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
        var (dict, _) = CreateDurableDict();
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
        var dict = CreateDetachedDict<long>();
        var buffer = new ArrayBufferWriter<byte>();

        // Act
        Action act = () => dict.WritePendingDiff(buffer);

        // Assert
        act.Should().Throw<ObjectDetachedException>();
    }
}
