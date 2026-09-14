// Fixed descriptor-v1 source decoding for the explicit schema-2 upgrade only.
using System.Buffers;
using System.Text.Json;
using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline;

public static partial class HistoryTimelineCanonicalCodec {
    internal sealed record LegacyDescriptorValue(HistorySegmentDescriptor Descriptor, string DescriptorDigest);

    private static byte[] EncodeLegacyDescriptorV1(LegacyDescriptorValue value) {
        HistorySegmentDescriptor descriptor = value.Descriptor;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) {
            writer.WriteStartObject();
            writer.WriteNumber("v", 1);
            writer.WriteString("timelineId", descriptor.TimelineId.Value);
            writer.WriteString(
                "partitionPolicyDigestAtCreation",
                descriptor.PartitionPolicyDigestAtCreation
            );
            writer.WriteString("rowId", descriptor.RowId.Value);
            if (descriptor.PreviousRowId is { } previousRowId) {
                writer.WriteString("previousRowId", previousRowId.Value);
            }
            else {
                writer.WriteNull("previousRowId");
            }
            WriteDescriptorRangeFields(writer, descriptor);
            writer.WriteString(
                "descriptorDigest",
                value.DescriptorDigest
            );
            writer.WriteEndObject();
        }
        return RequireEncodedBound(
            buffer.WrittenMemory.ToArray(),
            MaximumDescriptorUtf8Bytes,
            "history segment descriptor"
        );
    }

    internal static LegacyDescriptorValue DecodeLegacyDescriptorV1(
        ReadOnlySpan<byte> bytes
    ) => DecodeCanonical(
        bytes,
        MaximumDescriptorUtf8Bytes,
        static root => {
            RequireVersion(root, "history segment descriptor");
            var timelineId = new TimelineId(
                ReadString(root, "timelineId")
            );
            string policyDigest = ReadString(
                root,
                "partitionPolicyDigestAtCreation"
            );
            var rowId = new HistoryRowId(ReadString(root, "rowId"));
            HistoryRowId? previousRowId = ReadNullableString(
                root,
                "previousRowId"
            ) is { } previous
                ? new HistoryRowId(previous)
                : null;
            RefId refId = ReadRefId(root, "refId");
            EventAddress startExclusive = ReadAddress(
                root,
                "startExclusive"
            );
            EventAddress endInclusive = ReadAddress(
                root,
                "endInclusive"
            );
            SJ.SessionContextAnchorSetupReferences startSetups =
                ReadSetups(root, "startSetups");
            SJ.SessionContextAnchorSetupReferences endSetups =
                ReadSetups(root, "endSetups");
            string estimatorId = ReadString(
                root,
                "historyLoadEstimatorId"
            );
            var target = new HistoryLoadUnit(ReadInt64(
                root,
                "targetHistoryLoadAtCreation"
            ));
            var measured = new HistoryLoadUnit(ReadInt64(
                root,
                "measuredHistoryLoad"
            ));
            int rawEventCount = ReadInt32(root, "rawEventCount");
            int renderedBytes = ReadInt32(
                root,
                "measuredRenderedUtf8Bytes"
            );
            string rawRangeSha256 = ReadString(
                root,
                "rawRangeSha256"
            );
            string descriptorDigest = ReadString(root, "descriptorDigest");

            byte[] body = EncodeDescriptorBody(
                timelineId,
                policyDigest,
                previousRowId,
                refId,
                startExclusive,
                endInclusive,
                startSetups,
                endSetups,
                estimatorId,
                target,
                measured,
                rawEventCount,
                renderedBytes,
                rawRangeSha256
            );
            string expectedRowId = HistoryTimelineHash.Compute(
                HistoryTimelineHash.RowIdDomain,
                body
            );
            string expectedDescriptorDigest = HistoryTimelineHash.Compute(
                "atelia.history-timeline.descriptor.v1",
                body
            );
            if (!string.Equals(
                    expectedRowId,
                    rowId.Value,
                    StringComparison.Ordinal)
                || !string.Equals(
                    expectedDescriptorDigest,
                    descriptorDigest,
                    StringComparison.Ordinal)) {
                throw new InvalidDataException(
                    "History segment identity does not match its canonical body."
                );
            }
            return new LegacyDescriptorValue(new HistorySegmentDescriptor(
                timelineId,
                policyDigest,
                rowId,
                previousRowId,
                refId,
                startExclusive,
                endInclusive,
                startSetups,
                endSetups,
                estimatorId,
                target,
                measured,
                rawEventCount,
                renderedBytes,
                rawRangeSha256
            ), descriptorDigest);
        },
        EncodeLegacyDescriptorV1,
        "history segment descriptor"
    );

}
