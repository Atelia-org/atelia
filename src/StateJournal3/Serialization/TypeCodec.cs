using System.Buffers;

namespace Atelia.StateJournal3.Serialization;

public static class TypeCodec {
    internal static void EncodeCore(Type type) {
        throw new NotImplementedException();
    }
    public static void EncodeDict<TKey, TValue>(IBufferWriter<byte> writer) {
        throw new NotImplementedException();
    }
}
