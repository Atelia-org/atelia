using System.Buffers;

namespace Atelia.StateJournal.Serialization;

internal interface IValueOps<T> where T: notnull {
    static abstract bool Equals(T x, T y);

    // 用于作为类型特化容器的成员时。
    static abstract int WriteDataOnly(IBufferWriter<byte> writer, T value);

    // self-described，用于作为混杂存储容器成员时。
    static abstract int WriteTypedData(IBufferWriter<byte> writer, T value);
}
