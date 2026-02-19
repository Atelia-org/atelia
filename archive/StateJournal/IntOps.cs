
using System.Buffers;

namespace Atelia.StateJournal.Serialization;


internal struct Int32Ops : IValueOps<int> {

    public static bool Equals(int x, int y) => x == y;

    // 用于作为类型特化容器的成员时。
    public static int WriteDataOnly(IBufferWriter<byte> writer, int value) {

    }

    // self-described，用于作为混杂存储容器成员时。
    public static int WriteTypedData(IBufferWriter<byte> writer, int value) {

    }
}
