using System.Buffers;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Tests;

internal static class EstimateAssert {
    public static uint SerializedBodyBytes(Action<BinaryDiffWriter> write, Revision? revision = null) {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(buffer, revision);
        write(writer);
        return (uint)buffer.WrittenCount;
    }

    public static void EqualSerializedBodySize(uint estimatedBytes, Action<BinaryDiffWriter> write, Revision? revision = null) {
        Assert.Equal(SerializedBodyBytes(write, revision), estimatedBytes);
    }
}
