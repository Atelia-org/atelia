using System.Buffers;

namespace Atelia.StateJournal3;

public abstract class ValueBox : DurableBase {
    public bool TryGetValue<T>(out T value) where T : notnull {
        if (this is IValueBox<T> typed) {
            value = typed.Value;
            return true;
        }
        value = default!;
        return false;
    }

    // 用于作为类型特化容器的成员时。
    internal abstract int WriteDataOnly(IBufferWriter<byte> writer);

    // self-described，用于作为混杂存储容器成员时。
    internal abstract int WriteTypedData(IBufferWriter<byte> writer);
}

public abstract class IntegerBox : ValueBox {

    #region Canonical 无损，最短，优先无符号数
    public static IntegerBox Canonical(byte value) => new ByteBox(value);
    public static IntegerBox Canonical(sbyte value) => value >= 0 ? new ByteBox((byte)value) : new SByteBox(value);

    public static IntegerBox Canonical(ushort value) => value <= byte.MaxValue ? new ByteBox((byte)value) : new UInt16Box(value);
    protected static IntegerBox CanonicalRaw(short value) => value >= sbyte.MinValue ? new SByteBox((sbyte)value) : new Int16Box(value);
    public static IntegerBox Canonical(short value) => value >= 0 ? Canonical((ushort)value) : CanonicalRaw(value);

    public static IntegerBox Canonical(uint value) => value <= ushort.MaxValue ? Canonical((ushort)value) : new UInt32Box(value);
    protected static IntegerBox CanonicalRaw(int value) => value >= short.MinValue ? CanonicalRaw((short)value) : new Int32Box(value);
    public static IntegerBox Canonical(int value) => value >= 0 ? Canonical((uint)value) : CanonicalRaw(value);

    public static IntegerBox Canonical(ulong value) => value <= uint.MaxValue ? Canonical((uint)value) : new UInt64Box(value);
    public static IntegerBox Canonical(long value) => value >= 0 ? Canonical((ulong)value) : value >= int.MinValue ? CanonicalRaw((int)value) : new Int64Box(value);
    #endregion

    /// <summary>
    /// 当实例位于类型特化的泛型容器中时，由于统一使用同一种具体类型的<see cref="IntegerBox"/>, 通常为false。
    /// 当实例位于混杂（异构）存储的泛型容器中时（泛型参数为抽象基类），一定为true。
    /// </summary>
    public abstract bool IsCanonical { get; }

    /// <summary>在进入混杂存储的泛型容器时转换。</summary>
    public abstract IntegerBox ToCanonical { get; }
    public abstract byte? AsByte();
    public abstract sbyte? AsSByte();
    public abstract ushort? AsUInt16();
    public abstract short? AsInt16();
    public abstract uint? AsUInt32();
    public abstract int? AsInt32();
    public abstract ulong? AsUInt64();
    public abstract long? AsInt64();
}

public sealed class ByteBox : IntegerBox, IValueBox<Byte> {
    public override Type ContentType => typeof(Byte);
    public Byte Value { get; }

    public ByteBox(Byte value) { Value = value; }

    public override bool Equals(DurableBase? other) => (other is IValueBox<Byte> typed) && typed.Value == Value;

    public override bool IsCanonical => true;
    public override IntegerBox ToCanonical => this;

    public override byte? AsByte() => Value;
    public override sbyte? AsSByte() => Value <= sbyte.MaxValue ? (sbyte)Value : null;
    public override ushort? AsUInt16() => Value;
    public override short? AsInt16() => Value;
    public override uint? AsUInt32() => Value;
    public override int? AsInt32() => Value;
    public override ulong? AsUInt64() => Value;
    public override long? AsInt64() => Value;

    #region 隐式类型转换 用于类型特化的泛型容器
    public static implicit operator UInt16Box(ByteBox item) => new UInt16Box(item.Value);
    public static implicit operator Int16Box(ByteBox item) => new Int16Box(item.Value);
    public static implicit operator UInt32Box(ByteBox item) => new UInt32Box(item.Value);
    public static implicit operator Int32Box(ByteBox item) => new Int32Box(item.Value);
    public static implicit operator UInt64Box(ByteBox item) => new UInt64Box(item.Value);
    public static implicit operator Int64Box(ByteBox item) => new Int64Box(item.Value);
    #endregion

    public static explicit operator SByteBox(ByteBox item) => new SByteBox((sbyte)item.Value);

    internal override int WriteDataOnly(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }

    internal override int WriteTypedData(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}

public sealed class SByteBox : IntegerBox, IValueBox<SByte> {
    public override Type ContentType => typeof(SByte);
    public SByte Value { get; }

    public SByteBox(SByte value) { Value = value; }

    public override bool Equals(DurableBase? other) => (other is IValueBox<SByte> typed) && typed.Value == Value;

    public override bool IsCanonical => Value < 0;
    public override IntegerBox ToCanonical => IsCanonical ? this : new ByteBox((byte)Value);

    public override byte? AsByte() => 0 <= Value ? (byte)Value : null;
    public override sbyte? AsSByte() => Value;
    public override ushort? AsUInt16() => 0 <= Value ? (ushort)Value : null;
    public override short? AsInt16() => Value;
    public override uint? AsUInt32() => 0 <= Value ? (uint)Value : null;
    public override int? AsInt32() => Value;
    public override ulong? AsUInt64() => 0 <= Value ? (ulong)Value : null;
    public override long? AsInt64() => Value;

    public static implicit operator Int16Box(SByteBox item) => new Int16Box(item.Value);
    public static implicit operator Int32Box(SByteBox item) => new Int32Box(item.Value);
    public static implicit operator Int64Box(SByteBox item) => new Int64Box(item.Value);

    public static explicit operator ByteBox(SByteBox item) => new ByteBox((byte)item.Value);

    internal override int WriteDataOnly(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }

    internal override int WriteTypedData(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}

public sealed class UInt16Box : IntegerBox, IValueBox<UInt16> {
    public override Type ContentType => typeof(UInt16);
    public UInt16 Value { get; }

    public UInt16Box(UInt16 value) { Value = value; }

    public override bool Equals(DurableBase? other) => (other is IValueBox<UInt16> typed) && typed.Value == Value;

    public override bool IsCanonical => Value > byte.MaxValue;
    public override IntegerBox ToCanonical => IsCanonical ? this : new ByteBox((byte)Value);

    public override byte? AsByte() => Value <= byte.MaxValue ? (byte)Value : null;
    public override sbyte? AsSByte() => Value <= sbyte.MaxValue ? (sbyte)Value : null;
    public override ushort? AsUInt16() => Value;
    public override short? AsInt16() => Value <= short.MaxValue ? (short)Value : null;
    public override uint? AsUInt32() => Value;
    public override int? AsInt32() => Value;
    public override ulong? AsUInt64() => Value;
    public override long? AsInt64() => Value;

    public static implicit operator UInt32Box(UInt16Box item) => new UInt32Box(item.Value);
    public static implicit operator Int32Box(UInt16Box item) => new Int32Box(item.Value);
    public static implicit operator UInt64Box(UInt16Box item) => new UInt64Box(item.Value);
    public static implicit operator Int64Box(UInt16Box item) => new Int64Box(item.Value);

    public static explicit operator ByteBox(UInt16Box item) => new ByteBox((byte)item.Value); // 截断转换
    public static explicit operator Int16Box(UInt16Box item) => new Int16Box((short)item.Value); // 有无符号转换

    internal override int WriteDataOnly(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }

    internal override int WriteTypedData(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}
public sealed class Int16Box : IntegerBox, IValueBox<Int16> {
    public override Type ContentType => typeof(Int16);
    public Int16 Value { get; }

    public Int16Box(Int16 value) { Value = value; }

    public override bool Equals(DurableBase? other) => (other is IValueBox<Int16> typed) && typed.Value == Value;

    public override bool IsCanonical => Value < sbyte.MinValue;
    public override IntegerBox ToCanonical => IsCanonical ? this : Value < 0 ? new SByteBox((sbyte)Value) : Canonical((ushort)Value);

    public override byte? AsByte() => 0 <= Value && Value <= byte.MaxValue ? (byte)Value : null;
    public override sbyte? AsSByte() => sbyte.MinValue <= Value && Value <= sbyte.MaxValue ? (sbyte)Value : null;
    public override ushort? AsUInt16() => 0 <= Value ? (ushort)Value : null;
    public override short? AsInt16() => Value;
    public override uint? AsUInt32() => 0 <= Value ? (uint)Value : null;
    public override int? AsInt32() => Value;
    public override ulong? AsUInt64() => 0 <= Value ? (ulong)Value : null;
    public override long? AsInt64() => Value;

    public static implicit operator Int32Box(Int16Box item) => new Int32Box(item.Value);
    public static implicit operator Int64Box(Int16Box item) => new Int64Box(item.Value);

    public static explicit operator SByteBox(Int16Box item) => new SByteBox((sbyte)item.Value); // 截断转换
    public static explicit operator UInt16Box(Int16Box item) => new UInt16Box((ushort)item.Value); // 有无符号转换

    internal override int WriteDataOnly(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }

    internal override int WriteTypedData(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}

public sealed class UInt32Box : IntegerBox, IValueBox<UInt32> {
    public override Type ContentType => typeof(UInt32);
    public UInt32 Value { get; }

    public UInt32Box(UInt32 value) { Value = value; }

    public override bool Equals(DurableBase? other) => (other is IValueBox<UInt32> typed) && typed.Value == Value;

    public override bool IsCanonical => Value > ushort.MaxValue;
    public override IntegerBox ToCanonical => IsCanonical ? this : Canonical((ushort)Value);

    public override byte? AsByte() => Value <= byte.MaxValue ? (byte)Value : null;
    public override sbyte? AsSByte() => Value <= sbyte.MaxValue ? (sbyte)Value : null;
    public override ushort? AsUInt16() => Value <= ushort.MaxValue ? (ushort)Value : null;
    public override short? AsInt16() => Value <= short.MaxValue ? (short)Value : null;
    public override uint? AsUInt32() => Value;
    public override int? AsInt32() => Value <= int.MaxValue ? (int)Value : null;
    public override ulong? AsUInt64() => Value;
    public override long? AsInt64() => Value;

    public static implicit operator UInt64Box(UInt32Box item) => new UInt64Box(item.Value);
    public static implicit operator Int64Box(UInt32Box item) => new Int64Box(item.Value);

    // 截断转换
    public static explicit operator ByteBox(UInt32Box item) => new ByteBox((byte)item.Value);
    public static explicit operator UInt16Box(UInt32Box item) => new UInt16Box((ushort)item.Value);
    // 有无符号转换
    public static explicit operator Int32Box(UInt32Box item) => new Int32Box((int)item.Value);

    internal override int WriteDataOnly(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }

    internal override int WriteTypedData(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}

public sealed class Int32Box : IntegerBox, IValueBox<Int32> {
    public override Type ContentType => typeof(Int32);
    public Int32 Value { get; }

    public Int32Box(Int32 value) { Value = value; }

    public override bool Equals(DurableBase? other) => (other is IValueBox<Int32> typed) && typed.Value == Value;

    public override bool IsCanonical => Value < short.MinValue;
    public override IntegerBox ToCanonical => IsCanonical ? this : Value < 0 ? CanonicalRaw((short)Value) : Canonical((uint)Value);

    public override byte? AsByte() => 0 <= Value && Value <= byte.MaxValue ? (byte)Value : null;
    public override sbyte? AsSByte() => sbyte.MinValue <= Value && Value <= sbyte.MaxValue ? (sbyte)Value : null;
    public override ushort? AsUInt16() => 0 <= Value && Value <= ushort.MaxValue ? (ushort)Value : null;
    public override short? AsInt16() => short.MinValue <= Value && Value <= short.MaxValue ? (short)Value : null;
    public override uint? AsUInt32() => 0 <= Value ? (uint)Value : null;
    public override int? AsInt32() => Value;
    public override ulong? AsUInt64() => 0 <= Value ? (ulong)Value : null;
    public override long? AsInt64() => Value;

    public static implicit operator Int64Box(Int32Box item) => new Int64Box(item.Value);

    // 截断转换
    public static explicit operator SByteBox(Int32Box item) => new SByteBox((sbyte)item.Value);
    public static explicit operator Int16Box(Int32Box item) => new Int16Box((short)item.Value);
    // 有无符号转换
    public static explicit operator UInt32Box(Int32Box item) => new UInt32Box((uint)item.Value);

    internal override int WriteDataOnly(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }

    internal override int WriteTypedData(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}

public sealed class UInt64Box : IntegerBox, IValueBox<UInt64> {
    public override Type ContentType => typeof(UInt64);
    public UInt64 Value { get; }

    public UInt64Box(UInt64 value) { Value = value; }

    public override bool Equals(DurableBase? other) => (other is IValueBox<UInt64> typed) && typed.Value == Value;

    public override bool IsCanonical => Value > uint.MaxValue;
    public override IntegerBox ToCanonical => IsCanonical ? this : Canonical((uint)Value);

    public override byte? AsByte() => Value <= byte.MaxValue ? (byte)Value : null;
    public override sbyte? AsSByte() => Value <= (ulong)sbyte.MaxValue ? (sbyte)Value : null;
    public override ushort? AsUInt16() => Value <= ushort.MaxValue ? (ushort)Value : null;
    public override short? AsInt16() => Value <= (ulong)short.MaxValue ? (short)Value : null;
    public override uint? AsUInt32() => Value <= uint.MaxValue ? (uint)Value : null;
    public override int? AsInt32() => Value <= int.MaxValue ? (int)Value : null;
    public override ulong? AsUInt64() => Value;
    public override long? AsInt64() => Value <= long.MaxValue ? (long)Value : null;

    // 截断转换
    public static explicit operator ByteBox(UInt64Box item) => new ByteBox((byte)item.Value);
    public static explicit operator UInt16Box(UInt64Box item) => new UInt16Box((ushort)item.Value);
    public static explicit operator UInt32Box(UInt64Box item) => new UInt32Box((uint)item.Value);
    // 有无符号转换
    public static explicit operator Int64Box(UInt64Box item) => new Int64Box((long)item.Value);

    internal override int WriteDataOnly(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }

    internal override int WriteTypedData(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}

public sealed class Int64Box : IntegerBox, IValueBox<Int64> {
    public override Type ContentType => typeof(Int64);
    public Int64 Value { get; }

    public Int64Box(Int64 value) { Value = value; }

    public override bool Equals(DurableBase? other) => (other is IValueBox<Int64> typed) && typed.Value == Value;

    public override bool IsCanonical => Value < int.MinValue;
    public override IntegerBox ToCanonical => IsCanonical ? this : Value < 0 ? CanonicalRaw((int)Value) : Canonical((ulong)Value);

    public override byte? AsByte() => 0 <= Value && Value <= byte.MaxValue ? (byte)Value : null;
    public override sbyte? AsSByte() => sbyte.MinValue <= Value && Value <= sbyte.MaxValue ? (sbyte)Value : null;
    public override ushort? AsUInt16() => 0 <= Value && Value <= ushort.MaxValue ? (ushort)Value : null;
    public override short? AsInt16() => short.MinValue <= Value && Value <= short.MaxValue ? (short)Value : null;
    public override uint? AsUInt32() => 0 <= Value && Value <= uint.MaxValue ? (uint)Value : null;
    public override int? AsInt32() => int.MinValue <= Value && Value <= int.MaxValue ? (int)Value : null;
    public override ulong? AsUInt64() => 0 <= Value ? (ulong)Value : null;
    public override long? AsInt64() => Value;

    // 截断转换
    public static explicit operator SByteBox(Int64Box item) => new SByteBox((sbyte)item.Value);
    public static explicit operator Int16Box(Int64Box item) => new Int16Box((short)item.Value);
    public static explicit operator Int32Box(Int64Box item) => new Int32Box((int)item.Value);
    // 有无符号转换
    public static explicit operator UInt64Box(Int64Box item) => new UInt64Box((ulong)item.Value);

    internal override int WriteDataOnly(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }

    internal override int WriteTypedData(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}

public abstract class FloatBox : ValueBox {
    public abstract Half? GetHalf();
    public abstract float? GetSingle();
    public abstract double? GetDouble();
}

public class BooleanBox : ValueBox, IValueBox<bool> {
    public override Type ContentType => typeof(bool);

    public bool Value { get; }

    public BooleanBox(bool value) { Value = value; }

    public override bool Equals(DurableBase? other) => (other is IValueBox<bool> typed) && typed.Value == Value;

    internal override int WriteDataOnly(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }

    internal override int WriteTypedData(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}

public abstract class StringBox : ValueBox {

}
