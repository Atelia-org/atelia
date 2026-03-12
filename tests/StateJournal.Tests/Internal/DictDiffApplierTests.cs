using System.Buffers;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Internal.Tests;

public class DictDiffApplierTests {
    [Fact]
    public void BuildTyped_ReplaysRemoveAndUpsertSegments() {
        var bodyWriter = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(bodyWriter);

        writer.WriteCount(1); // remove count
        writer.BareInt32(1, true);
        writer.WriteCount(2); // upsert count
        writer.BareInt32(2, true);
        writer.BareInt64(20, false);
        writer.BareInt32(3, true);
        writer.BareInt64(300, false);

        var target = new Dictionary<int, long> {
            [1] = 10,
            [3] = 30,
        };

        var reader = new BinaryDiffReader(bodyWriter.WrittenSpan);
        DictDiffApplier.Apply<int, long, Int32Helper, Int64Helper>(ref reader, target);

        Assert.False(target.ContainsKey(1));
        Assert.Equal(20L, target[2]);
        Assert.Equal(300L, target[3]);
    }

    [Fact]
    public void BuildMixed_ReplaysTaggedScalarValues() {
        var bodyWriter = new ArrayBufferWriter<byte>();
        var writer = new BinaryDiffWriter(bodyWriter);

        writer.WriteCount(1); // remove count
        writer.BareInt32(1, true);
        writer.WriteCount(3); // upsert count
        writer.BareInt32(2, true);
        writer.TaggedBoolean(true);
        writer.BareInt32(3, true);
        writer.TaggedNegativeInteger(-25);
        writer.BareInt32(4, true);
        writer.TaggedFloatingPoint(1.5);

        var target = new Dictionary<int, ValueBox> {
            [1] = ValueBox.BooleanFace.False,
            [3] = ValueBox.Int64Face.From(0),
        };
        var reader = new BinaryDiffReader(bodyWriter.WrittenSpan);
        DictDiffApplier.Apply<int, ValueBox, Int32Helper, ValueBoxHelper>(ref reader, target);

        Assert.False(target.ContainsKey(1));
        Assert.Equal(GetIssue.None, ValueBox.BooleanFace.Get(target[2], out bool boolValue));
        Assert.True(boolValue);
        Assert.Equal(GetIssue.None, ValueBox.Int64Face.Get(target[3], out long intValue));
        Assert.Equal(-25L, intValue);
        Assert.Equal(GetIssue.None, ValueBox.HalfFace.Get(target[4], out Half halfValue));
        Assert.Equal(BitConverter.HalfToUInt16Bits((Half)1.5), BitConverter.HalfToUInt16Bits(halfValue));
    }
}
