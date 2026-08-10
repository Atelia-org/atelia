using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.EventJournal;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

internal static class HistoryTimelineTestData {
    internal static readonly TimelineId Timeline = new(
        "00112233445566778899aabbccddeeff"
    );

    internal static EventAddress Address(ulong value) => new(
        SizedPtr.FromPacked(value),
        1,
        AddressHint.None
    );

    internal static SJ.SessionContextAnchorSetupReferences Setups(
        ulong seed = 10
    ) => new(
        new SJ.SessionContextSetupReference(
            Address(seed),
            2,
            new string('a', 64)
        ),
        new SJ.SessionContextSetupReference(
            Address(seed + 1),
            1,
            new string('b', 64)
        )
    );

    internal static PartitionPolicyRevision Policy(
        string estimatorId,
        long target = 5,
        int maxRawEvents = 100,
        int maxRenderedBytes = 100
    ) => PartitionPolicyRevision.Create(
        Timeline,
        HistoryPartitionAlgorithms
            .FirstReplaySafeBoundaryAtTargetV1,
        estimatorId,
        new HistoryLoadUnit(target),
        maxRawEvents,
        maxRenderedBytes
    );

    internal static SJ.SessionHistoryPlanningWindow Window(
        string[] unitContents,
        (int RawOffset, int CompletedUnitCount)[] boundaries,
        int rawEventCount = 0
    ) {
        int count = rawEventCount == 0
            ? boundaries.Max(static item => item.RawOffset)
            : rawEventCount;
        EventAddress start = Address(100);
        EventAddress[] raw = [.. Enumerable.Range(1, count)
            .Select(offset => Address((ulong)(100 + offset)))];
        SJ.SessionContextAnchorSetupReferences setups = Setups();
        SJ.SessionHistoryPlanningBoundary[] replaySafe = [..
            boundaries.Select(item =>
                new SJ.SessionHistoryPlanningBoundary(
                    raw[item.RawOffset - 1],
                    item.CompletedUnitCount
                ))
        ];
        var setupMap = replaySafe.ToDictionary(
            static boundary => boundary.Address,
            _ => setups
        );
        SJ.SessionHistoryPlanningUnit[] units = [..
            unitContents.Select((content, index) =>
                new SJ.SessionHistoryPlanningUnit(
                    new ObservationMessage(content),
                    raw[Math.Min(index, raw.Length - 1)],
                    raw[Math.Min(index, raw.Length - 1)]
                ))
        ];
        return new SJ.SessionHistoryPlanningWindow(
            raw[^1],
            start,
            setups,
            setups,
            raw,
            units,
            replaySafe,
            setupMap,
            new SJ.SessionHistoryPlanningDiagnostics(
                HeaderVisits: count,
                PayloadReads: count,
                DecodedPayloadBytes: count,
                DecodedEventCount: count
            )
        ) {
            RawRangeSha256 = new string('f', 64)
        };
    }

    internal static HistoryLoadBaseline Baseline(
        SJ.SessionHistoryPlanningWindow window
    ) => HistoryLoadBaselineResolver.Resolve(
        window.StartExclusive,
        window.Units.Count,
        window.ReplaySafeBoundaries,
        window.StartExclusive
    );

    internal sealed class DelegateEstimator(
        string id,
        Func<SJ.SessionHistoryPlanningUnit, HistoryUnitLoadMeasurement>
            measure
    ) : IHistoryUnitLoadEstimator {
        public string Id { get; } = id;
        public int CallCount { get; private set; }

        public HistoryUnitLoadMeasurement Measure(
            SJ.SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) {
            CallCount++;
            return measure(unit);
        }
    }

    internal static DelegateEstimator NumericEstimator(
        int renderedBytes = 1,
        string id = "atelia.tests.history-load.numeric-v1"
    ) => new(
        id,
        unit => new HistoryUnitLoadMeasurement(
            new HistoryLoadUnit(long.Parse(
                ((ObservationMessage)unit.Message).Content!
            )),
            renderedBytes
        )
    );
}
