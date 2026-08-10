using System.Text;
using System.Runtime.InteropServices;
using Atelia.EventJournal;
using static Atelia.SessionJournal.HistoryTimeline.Tests.HistoryTimelineTestData;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class HistoryTimelineContractAndCodecTests {
    [Fact]
    public void PolicyCanonicalBytesAndDigestAreGolden() {
        PartitionPolicyRevision policy = Policy(
            "atelia.tests.history-load.numeric-v1"
        );

        byte[] bytes = policy.ToCanonicalBytes();
        PartitionPolicyRevision decoded =
            HistoryTimelineCanonicalCodec.DecodePartitionPolicy(bytes);

        Assert.Equal(policy, decoded);
        Assert.Equal(
            "102307b45502e4e3d9d47e0c3b3065449838a66f757d4283d8d06e4e3a431f48",
            policy.PolicyDigest
        );
        Assert.Equal(
            "{\"v\":1,\"timelineId\":\"00112233445566778899aabbccddeeff\","
            + "\"partitionAlgorithmId\":\"atelia.history-timeline.partition.first-replay-safe-at-target.v1\","
            + "\"historyLoadEstimatorId\":\"atelia.tests.history-load.numeric-v1\","
            + "\"targetHistoryLoad\":5,\"maxRawEvents\":100,"
            + "\"maxRenderedBytes\":100,\"policyDigest\":\""
            + policy.PolicyDigest
            + "\"}",
            Encoding.UTF8.GetString(bytes)
        );
    }

    [Fact]
    public void DescriptorUsesBoundSelectedRangeAndDistinctHashDomains() {
        PartitionPolicyRevision policy = Policy(
            "atelia.tests.history-load.numeric-v1"
        );
        HistoryPartitionPoint point = Point(policy);
        const string selectedRangeSha =
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        var bound = new BoundHistorySegmentRange(
            new RefId(1),
            point.StartExclusive,
            point.EndInclusive,
            point.StartSetups,
            point.EndSetups,
            point.BaselineCompletedUnitCount,
            point.EndCompletedUnitCount,
            point.RawEventCount,
            selectedRangeSha
        );

        HistorySegmentDescriptor descriptor =
            HistorySegmentDescriptorFactory.Create(
                point,
                bound,
                policy,
                predecessor: null
            );
        byte[] canonical = descriptor.ToCanonicalBytes();
        HistorySegmentDescriptor decoded =
            HistoryTimelineCanonicalCodec
                .DecodeHistorySegmentDescriptor(canonical);

        Assert.Equal(selectedRangeSha, descriptor.RawRangeSha256);
        Assert.NotEqual(
            descriptor.RowId.Value,
            descriptor.DescriptorDigest.Value
        );
        Assert.Equal(descriptor, decoded);
        Assert.Equal(
            "f945064a1db7c0f70a1ca3f3e0f0a44f56f9d3b7e1880e6ea4a8ef48528f75cd",
            descriptor.RowId.Value
        );
        Assert.Equal(
            "2b2c36201f3b38d7d82855bc98e9dc55b716c950078c480e1f797662937db65b",
            descriptor.DescriptorDigest.Value
        );
    }

    [Fact]
    public void DescriptorFactoryRejectsEveryMismatchedBoundFact() {
        PartitionPolicyRevision policy = Policy(
            "atelia.tests.history-load.numeric-v1"
        );
        HistoryPartitionPoint point = new(
            policy.TimelineId,
            policy.PolicyDigest,
            Address(100),
            Address(101),
            Setups(),
            Setups(),
            baselineCompletedUnitCount: 1,
            endCompletedUnitCount: 3,
            new HistoryLoadUnit(5),
            rawEventCount: 2,
            measuredRenderedUtf8Bytes: 1
        );
        BoundHistorySegmentRange Range(
            EventAddress? startExclusive = null,
            EventAddress? endInclusive = null,
            SJ.SessionContextAnchorSetupReferences? startSetups = null,
            SJ.SessionContextAnchorSetupReferences? endSetups = null,
            int? baselineCompletedUnitCount = null,
            int? endCompletedUnitCount = null,
            int? rawEventCount = null
        ) => new(
            new RefId(1),
            startExclusive ?? point.StartExclusive,
            endInclusive ?? point.EndInclusive,
            startSetups ?? point.StartSetups,
            endSetups ?? point.EndSetups,
            baselineCompletedUnitCount
                ?? point.BaselineCompletedUnitCount,
            endCompletedUnitCount ?? point.EndCompletedUnitCount,
            rawEventCount ?? point.RawEventCount,
            new string('c', 64)
        );

        Assert.Throws<InvalidDataException>(() =>
            HistorySegmentDescriptorFactory.Create(
                point,
                Range(startExclusive: Address(99)),
                policy,
                null
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            HistorySegmentDescriptorFactory.Create(
                point,
                Range(endInclusive: Address(999)),
                policy,
                null
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            HistorySegmentDescriptorFactory.Create(
                point,
                Range(startSetups: Setups(20)),
                policy,
                null
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            HistorySegmentDescriptorFactory.Create(
                point,
                Range(endSetups: Setups(20)),
                policy,
                null
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            HistorySegmentDescriptorFactory.Create(
                point,
                Range(baselineCompletedUnitCount: 0),
                policy,
                null
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            HistorySegmentDescriptorFactory.Create(
                point,
                Range(endCompletedUnitCount: 2),
                policy,
                null
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            HistorySegmentDescriptorFactory.Create(
                point,
                Range(rawEventCount: 1),
                policy,
                null
            )
        );
    }

    [Fact]
    public void PolicyChangeDoesNotReinterpretSealedDescriptor() {
        const string estimatorId =
            "atelia.tests.history-load.numeric-v1";
        PartitionPolicyRevision sixty = Policy(
            estimatorId,
            target: 60
        );
        HistorySegmentDescriptor sealedAtSixty = Descriptor(
            sixty,
            measuredLoad: 100,
            rangeHash: new string('6', 64)
        );
        byte[] originalBytes = sealedAtSixty.ToCanonicalBytes();

        PartitionPolicyRevision ninety = Policy(
            estimatorId,
            target: 90
        );
        HistorySegmentDescriptor sealedAtNinety = Descriptor(
            ninety,
            measuredLoad: 100,
            rangeHash: new string('9', 64)
        );

        Assert.Equal(60, sealedAtSixty.TargetHistoryLoadAtCreation.Value);
        Assert.Equal(originalBytes, sealedAtSixty.ToCanonicalBytes());
        Assert.NotEqual(sealedAtSixty.RowId, sealedAtNinety.RowId);
        Assert.NotEqual(
            sealedAtSixty.DescriptorDigest,
            sealedAtNinety.DescriptorDigest
        );
    }

    [Fact]
    public void PolicyDigestAloneParticipatesInDescriptorIdentity() {
        const string estimatorId =
            "atelia.tests.history-load.numeric-v1";
        PartitionPolicyRevision firstPolicy = Policy(
            estimatorId,
            maxRawEvents: 100
        );
        PartitionPolicyRevision secondPolicy = Policy(
            estimatorId,
            maxRawEvents: 101
        );

        HistorySegmentDescriptor first = Descriptor(
            firstPolicy,
            measuredLoad: 5,
            rangeHash: new string('a', 64)
        );
        HistorySegmentDescriptor second = Descriptor(
            secondPolicy,
            measuredLoad: 5,
            rangeHash: new string('a', 64)
        );

        Assert.NotEqual(
            first.PartitionPolicyDigestAtCreation,
            second.PartitionPolicyDigestAtCreation
        );
        Assert.Equal(first.RefId, second.RefId);
        Assert.Equal(first.StartExclusive, second.StartExclusive);
        Assert.Equal(first.EndInclusive, second.EndInclusive);
        Assert.Equal(
            first.TargetHistoryLoadAtCreation,
            second.TargetHistoryLoadAtCreation
        );
        Assert.NotEqual(first.RowId, second.RowId);
        Assert.NotEqual(
            first.DescriptorDigest,
            second.DescriptorDigest
        );
    }

    [Fact]
    public void PreviousRowIdAloneParticipatesInDescriptorIdentity() {
        PartitionPolicyRevision policy = Policy(
            "atelia.tests.history-load.numeric-v1"
        );
        SJ.SessionContextAnchorSetupReferences setups = Setups();
        var predecessorPoint = new HistoryPartitionPoint(
            policy.TimelineId,
            policy.PolicyDigest,
            Address(99),
            Address(100),
            setups,
            setups,
            baselineCompletedUnitCount: 0,
            endCompletedUnitCount: 1,
            new HistoryLoadUnit(5),
            rawEventCount: 1,
            measuredRenderedUtf8Bytes: 1
        );
        HistorySegmentDescriptor predecessor =
            HistorySegmentDescriptorFactory.Create(
                predecessorPoint,
                new BoundHistorySegmentRange(
                    new RefId(1),
                    predecessorPoint.StartExclusive,
                    predecessorPoint.EndInclusive,
                    setups,
                    setups,
                    0,
                    1,
                    1,
                    new string('0', 64)
                ),
                policy,
                predecessor: null
            );
        HistoryPartitionPoint point = Point(policy);
        var range = new BoundHistorySegmentRange(
            new RefId(1),
            point.StartExclusive,
            point.EndInclusive,
            point.StartSetups,
            point.EndSetups,
            point.BaselineCompletedUnitCount,
            point.EndCompletedUnitCount,
            point.RawEventCount,
            new string('1', 64)
        );

        HistorySegmentDescriptor withoutPrevious =
            HistorySegmentDescriptorFactory.Create(
                point,
                range,
                policy,
                predecessor: null
            );
        HistorySegmentDescriptor withPrevious =
            HistorySegmentDescriptorFactory.Create(
                point,
                range,
                policy,
                predecessor
            );

        Assert.Null(withoutPrevious.PreviousRowId);
        Assert.Equal(predecessor.RowId, withPrevious.PreviousRowId);
        Assert.Equal(
            withoutPrevious.RawRangeSha256,
            withPrevious.RawRangeSha256
        );
        Assert.NotEqual(withoutPrevious.RowId, withPrevious.RowId);
        Assert.NotEqual(
            withoutPrevious.DescriptorDigest,
            withPrevious.DescriptorDigest
        );
    }

    [Fact]
    public void PreviousRowParticipatesInIdentityAndProposalBinding() {
        PartitionPolicyRevision policy = Policy(
            "atelia.tests.history-load.numeric-v1"
        );
        HistorySegmentDescriptor first = Descriptor(
            policy,
            measuredLoad: 5,
            rangeHash: new string('1', 64)
        );
        HistoryPartitionPoint secondPoint = new(
            policy.TimelineId,
            policy.PolicyDigest,
            first.EndInclusive,
            Address(102),
            first.EndSetups,
            first.EndSetups,
            baselineCompletedUnitCount: 1,
            endCompletedUnitCount: 2,
            new HistoryLoadUnit(5),
            rawEventCount: 1,
            measuredRenderedUtf8Bytes: 1
        );
        var secondRange = new BoundHistorySegmentRange(
            first.RefId,
            secondPoint.StartExclusive,
            secondPoint.EndInclusive,
            secondPoint.StartSetups,
            secondPoint.EndSetups,
            secondPoint.BaselineCompletedUnitCount,
            secondPoint.EndCompletedUnitCount,
            secondPoint.RawEventCount,
            new string('2', 64)
        );
        HistorySegmentDescriptor second =
            HistorySegmentDescriptorFactory.Create(
                secondPoint,
                secondRange,
                policy,
                first
            );
        var head = new TimelineHeadRef(
            policy.TimelineId,
            first.RefId,
            first.RowId,
            policy.PolicyDigest,
            first.EndInclusive,
            generation: 1
        );
        var proposal = new HistoryRowProposal(
            head,
            second.EndInclusive,
            second
        );

        Assert.Equal(first.RowId, second.PreviousRowId);
        Assert.Equal(second, proposal.Descriptor);
        Assert.Equal(
            second.ToCanonicalBytes(),
            proposal.CanonicalDescriptorBytes.ToArray()
        );
        ReadOnlyMemory<byte> exposed = proposal.CanonicalDescriptorBytes;
        Assert.True(MemoryMarshal.TryGetArray(
            exposed,
            out ArraySegment<byte> backing
        ));
        backing.Array![backing.Offset] ^= 0xff;
        Assert.Equal(
            second.ToCanonicalBytes(),
            proposal.CanonicalDescriptorBytes.ToArray()
        );
    }

    [Fact]
    public void TimelineHeadRefEnforcesCanonicalEmptyAndNonEmptyStates() {
        PartitionPolicyRevision policy = Policy(
            "atelia.tests.history-load.numeric-v1"
        );
        var empty = new TimelineHeadRef(
            policy.TimelineId,
            new RefId(1),
            headRowId: null,
            policy.PolicyDigest,
            selectedRawHeadAtCommit: null,
            generation: 0
        );
        Assert.Null(empty.HeadRowId);

        var policyOnlyCasHead = new TimelineHeadRef(
            policy.TimelineId,
            new RefId(1),
            headRowId: null,
            policy.PolicyDigest,
            selectedRawHeadAtCommit: null,
            generation: 1
        );
        Assert.Equal(1, policyOnlyCasHead.Generation);

        Assert.Throws<ArgumentException>(() => new TimelineHeadRef(
            policy.TimelineId,
            new RefId(1),
            headRowId: null,
            policy.PolicyDigest,
            selectedRawHeadAtCommit: Address(101),
            generation: 0
        ));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimelineHeadRef(
            policy.TimelineId,
            new RefId(1),
            headRowId: null,
            policy.PolicyDigest,
            selectedRawHeadAtCommit: null,
            generation: -1
        ));

        HistorySegmentDescriptor descriptor = Descriptor(
            policy,
            measuredLoad: 5,
            rangeHash: new string('1', 64)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TimelineHeadRef(
                policy.TimelineId,
                descriptor.RefId,
                descriptor.RowId,
                policy.PolicyDigest,
                descriptor.EndInclusive,
                generation: 0
            )
        );
    }

    [Fact]
    public void RowProposalRejectsEachExpectedHeadMismatchIndependently() {
        PartitionPolicyRevision policy = Policy(
            "atelia.tests.history-load.numeric-v1"
        );
        HistorySegmentDescriptor descriptor = Descriptor(
            policy,
            measuredLoad: 5,
            rangeHash: new string('1', 64)
        );
        TimelineHeadRef Head(
            TimelineId? timelineId = null,
            RefId? refId = null,
            HistoryRowId? rowId = null,
            string? policyDigest = null
        ) => new(
            timelineId ?? policy.TimelineId,
            refId ?? descriptor.RefId,
            rowId,
            policyDigest ?? policy.PolicyDigest,
            rowId is null ? null : descriptor.EndInclusive,
            rowId is null ? 0 : 1
        );

        _ = new HistoryRowProposal(
            Head(),
            descriptor.EndInclusive,
            descriptor
        );
        Assert.Throws<InvalidDataException>(() =>
            new HistoryRowProposal(
                Head(timelineId: new TimelineId(
                    "ffeeddccbbaa99887766554433221100"
                )),
                descriptor.EndInclusive,
                descriptor
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            new HistoryRowProposal(
                Head(refId: new RefId(2)),
                descriptor.EndInclusive,
                descriptor
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            new HistoryRowProposal(
                Head(rowId: new HistoryRowId(new string('2', 64))),
                descriptor.EndInclusive,
                descriptor
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            new HistoryRowProposal(
                Head(policyDigest: new string('3', 64)),
                descriptor.EndInclusive,
                descriptor
            )
        );
    }

    [Fact]
    public void PolicyDecoderRejectsEveryNonCanonicalShape() {
        PartitionPolicyRevision policy = Policy(
            "atelia.tests.history-load.numeric-v1"
        );
        string canonical = Encoding.UTF8.GetString(
            policy.ToCanonicalBytes()
        );
        string withoutOpeningVersion = canonical[7..^1];
        string[] invalid = [
            " " + canonical,
            canonical + "\n",
            canonical[..^1] + ",\"unknown\":1}",
            "{\"v\":1," + canonical[1..],
            canonical.Replace(
                "\"timelineId\":\"00112233445566778899aabbccddeeff\"",
                "\"timelineId\":null",
                StringComparison.Ordinal
            ),
            canonical.Replace(
                "00112233445566778899aabbccddeeff",
                "00112233445566778899AABBCCDDEEFF",
                StringComparison.Ordinal
            ),
            canonical.Replace(
                "\"timelineId\"",
                "\"TimelineId\"",
                StringComparison.Ordinal
            ),
            canonical.Replace(
                "atelia.tests.history-load.numeric-v1",
                "bad\\uD800",
                StringComparison.Ordinal
            ),
            "{" + withoutOpeningVersion + ",\"v\":1}"
        ];

        Assert.All(invalid, text =>
            Assert.Throws<InvalidDataException>(() =>
                HistoryTimelineCanonicalCodec.DecodePartitionPolicy(
                    Encoding.UTF8.GetBytes(text)
                )
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            HistoryTimelineCanonicalCodec.DecodePartitionPolicy(
                [0x7b, 0x22, 0xff, 0x22, 0x7d]
            )
        );
    }

    [Fact]
    public void DescriptorDecoderRejectsNonCanonicalAndForgedIdentity() {
        HistorySegmentDescriptor descriptor = Descriptor(
            Policy("atelia.tests.history-load.numeric-v1"),
            measuredLoad: 5,
            rangeHash: new string('d', 64)
        );
        string canonical = Encoding.UTF8.GetString(
            descriptor.ToCanonicalBytes()
        );
        string[] invalid = [
            "\n" + canonical,
            canonical[..^1] + ",\"unknown\":true}",
            "{\"v\":1," + canonical[1..],
            canonical.Replace(
                descriptor.RowId.Value,
                new string('0', 64),
                StringComparison.Ordinal
            ),
            canonical.Replace(
                "\"descriptorDigest\":\"",
                "\"descriptorDigest\":null,\"ignored\":\"",
                StringComparison.Ordinal
            )
        ];

        Assert.All(invalid, text =>
            Assert.Throws<InvalidDataException>(() =>
                HistoryTimelineCanonicalCodec
                    .DecodeHistorySegmentDescriptor(
                        Encoding.UTF8.GetBytes(text)
                    )
            )
        );
    }

    [Fact]
    public void BoundsAndTypedSyntaxFailClosed() {
        Assert.Throws<ArgumentException>(() =>
            new TimelineId("00112233445566778899AABBCCDDEEFF")
        );
        Assert.Throws<ArgumentException>(() =>
            new HistoryRowId(new string('g', 64))
        );
        Assert.Throws<ArgumentException>(() =>
            new HistorySegmentDescriptorDigest(new string('G', 64))
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PartitionPolicyRevision.Create(
                Timeline,
                new string('x', 129),
                "estimator",
                new HistoryLoadUnit(1),
                1,
                1
            )
        );
        Assert.Throws<ArgumentException>(() =>
            PartitionPolicyRevision.Create(
                Timeline,
                "invalid-\uD800",
                "estimator",
                new HistoryLoadUnit(1),
                1,
                1
            )
        );
        _ = PartitionPolicyRevision.Create(
            Timeline,
            HistoryPartitionAlgorithms
                .FirstReplaySafeBoundaryAtTargetV1,
            "estimator",
            new HistoryLoadUnit(1),
            HistoryPartitionPolicyLimits.MaximumRawEvents,
            HistoryPartitionPolicyLimits.MaximumRenderedBytes
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PartitionPolicyRevision.Create(
                Timeline,
                HistoryPartitionAlgorithms
                    .FirstReplaySafeBoundaryAtTargetV1,
                "estimator",
                new HistoryLoadUnit(1),
                HistoryPartitionPolicyLimits.MaximumRawEvents + 1,
                1
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PartitionPolicyRevision.Create(
                Timeline,
                HistoryPartitionAlgorithms
                    .FirstReplaySafeBoundaryAtTargetV1,
                "estimator",
                new HistoryLoadUnit(1),
                1,
                HistoryPartitionPolicyLimits.MaximumRenderedBytes + 1
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            HistoryTimelineCanonicalCodec.DecodePartitionPolicy(
                new byte[
                    HistoryTimelineCanonicalCodec
                        .MaximumPolicyUtf8Bytes + 1
                ]
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            HistoryTimelineCanonicalCodec
                .DecodeHistorySegmentDescriptor(
                    new byte[
                        HistoryTimelineCanonicalCodec
                            .MaximumDescriptorUtf8Bytes + 1
                    ]
                )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundHistorySegmentRange(
                new RefId(1),
                Address(100),
                Address(101),
                Setups() with {
                    RuntimeConfig = Setups().RuntimeConfig with {
                        BodySchemaVersion = 0
                    }
                },
                Setups(),
                0,
                1,
                1,
                new string('a', 64)
            )
        );
        Assert.Throws<ArgumentNullException>(() =>
            new HistoryPartitionResult.Selected(null!)
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HistoryPartitionResult.NotEnough(
                new HistoryLoadUnit(0),
                rawEventCount: -1,
                completedUnitCount: 0,
                measuredRenderedUtf8Bytes: 0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HistoryPartitionResult.LimitExceeded(
                (HistoryPartitionLimitKind)999,
                new HistoryLoadUnit(0),
                rawEventCount: 0,
                completedUnitCount: 0,
                measuredRenderedUtf8Bytes: 0
            )
        );
    }

    private static HistoryPartitionPoint Point(
        PartitionPolicyRevision policy,
        long measuredLoad = 5
    ) => new(
        policy.TimelineId,
        policy.PolicyDigest,
        Address(100),
        Address(101),
        Setups(),
        Setups(),
        baselineCompletedUnitCount: 0,
        endCompletedUnitCount: 1,
        new HistoryLoadUnit(measuredLoad),
        rawEventCount: 1,
        measuredRenderedUtf8Bytes: 1
    );

    private static HistorySegmentDescriptor Descriptor(
        PartitionPolicyRevision policy,
        long measuredLoad,
        string rangeHash
    ) {
        HistoryPartitionPoint point = Point(policy, measuredLoad);
        return HistorySegmentDescriptorFactory.Create(
            point,
            new BoundHistorySegmentRange(
                new RefId(1),
                point.StartExclusive,
                point.EndInclusive,
                point.StartSetups,
                point.EndSetups,
                point.BaselineCompletedUnitCount,
                point.EndCompletedUnitCount,
                point.RawEventCount,
                rangeHash
            ),
            policy,
            predecessor: null
        );
    }
}
