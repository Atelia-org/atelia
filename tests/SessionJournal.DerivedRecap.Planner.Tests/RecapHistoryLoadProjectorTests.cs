using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.EventJournal;
using Xunit;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.DerivedRecap.Planner.Tests;

public sealed class RecapHistoryLoadProjectorTests {
    [Fact]
    public void StartBaselineProjectsAdditivePrefixAndSharedCounts() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        var estimator = new ContentEstimator();

        RecapHistoryLoadMeasurement measured =
            RecapHistoryLoadProjector.Measure(
                window,
                window.StartExclusive,
                estimator
            );

        Assert.Equal(ContentEstimator.IdValue, measured.EstimatorId);
        Assert.Equal(window.StartExclusive, measured.BaselineAddress);
        Assert.Equal(0, measured.BaselineCompletedUnitCount);
        Assert.Equal(10, measured.Growth.Value);
        Assert.Equal(3, measured.RenderedUtf8Bytes);
        Assert.Equal(3, estimator.CallCount);
        Assert.Equal(
            [2L, 2L, 5L, 5L, 10L, 10L],
            measured.ReplaySafeBoundaries
                .Select(static item =>
                    item.AbsorbedSinceBaseline.Value
                )
        );
        Assert.Equal(
            [1, 1, 2, 2, 3, 3],
            measured.ReplaySafeBoundaries
                .Select(static item =>
                    item.HistoryUnitCountSinceBaseline
                )
        );
    }

    [Fact]
    public void ExactBoundaryAddressCutsSharedCountAndOldPrefix() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        EventAddress baseline =
            window.ReplaySafeBoundaries[1].Address;
        var estimator = new ContentEstimator(
            failContent: "2"
        );

        RecapHistoryLoadMeasurement measured =
            RecapHistoryLoadProjector.Measure(
                window,
                baseline,
                estimator
            );

        Assert.Equal(1, measured.BaselineCompletedUnitCount);
        Assert.Equal(8, measured.Growth.Value);
        Assert.Equal(2, measured.RenderedUtf8Bytes);
        Assert.Equal(2, estimator.CallCount);
        Assert.Equal(
            window.ReplaySafeBoundaries
                .Skip(2)
                .Select(static boundary => boundary.Address),
            measured.ReplaySafeBoundaries
                .Select(static boundary => boundary.Address)
        );
        Assert.Equal(
            [3L, 3L, 8L, 8L],
            measured.ReplaySafeBoundaries.Select(static boundary =>
                boundary.AbsorbedSinceBaseline.Value
            )
        );
    }

    [Fact]
    public void BaselineResolverPreservesExactSharedCountOrdinal() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();

        RecapHistoryLoadBaseline first =
            RecapHistoryLoadBaselineResolver.Resolve(
                window.StartExclusive,
                window.Units.Count,
                window.ReplaySafeBoundaries,
                window.ReplaySafeBoundaries[0].Address
            );
        RecapHistoryLoadBaseline second =
            RecapHistoryLoadBaselineResolver.Resolve(
                window.StartExclusive,
                window.Units.Count,
                window.ReplaySafeBoundaries,
                window.ReplaySafeBoundaries[1].Address
            );

        Assert.Equal(1, first.CompletedUnitCount);
        Assert.Equal(1, first.FirstLaterBoundaryIndex);
        Assert.Equal(1, second.CompletedUnitCount);
        Assert.Equal(2, second.FirstLaterBoundaryIndex);
    }

    [Fact]
    public void ContentFreeResolverMatchesEveryProjectedBaseline() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        EventAddress[] baselines = [
            window.StartExclusive,
            .. window.ReplaySafeBoundaries.Select(
                static boundary => boundary.Address
            )
        ];

        foreach (EventAddress baselineAddress in baselines) {
            RecapHistoryLoadBaseline resolved =
                RecapHistoryLoadBaselineResolver.Resolve(
                    window.StartExclusive,
                    window.Units.Count,
                    window.ReplaySafeBoundaries,
                    baselineAddress
                );
            RecapHistoryLoadMeasurement projected =
                RecapHistoryLoadProjector.Measure(
                    window,
                    baselineAddress,
                    new ContentEstimator()
                );

            Assert.Equal(
                resolved.CompletedUnitCount,
                projected.BaselineCompletedUnitCount
            );
            Assert.Equal(
                window.ReplaySafeBoundaries
                    .Skip(resolved.FirstLaterBoundaryIndex)
                    .Select(static boundary => boundary.Address),
                projected.ReplaySafeBoundaries
                    .Select(static boundary => boundary.Address)
            );
        }
    }

    [Fact]
    public void EarlierSharedCountBoundaryStillEmitsZeroLoadBoundary() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        EventAddress baseline =
            window.ReplaySafeBoundaries[0].Address;

        RecapHistoryLoadMeasurement measured =
            RecapHistoryLoadProjector.Measure(
                window,
                baseline,
                new ContentEstimator()
            );

        RecapHistoryLoadBoundary first =
            measured.ReplaySafeBoundaries[0];
        Assert.Equal(
            window.ReplaySafeBoundaries[1].Address,
            first.Address
        );
        Assert.Equal(0, first.HistoryUnitCountSinceBaseline);
        Assert.Equal(0, first.AbsorbedSinceBaseline.Value);
    }

    [Fact]
    public void RangeAdditivityMatchesIndependentSuffixes() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        var estimator = new ContentEstimator();
        RecapHistoryLoadMeasurement whole =
            RecapHistoryLoadProjector.Measure(
                window,
                window.StartExclusive,
                estimator
            );
        EventAddress split =
            window.ReplaySafeBoundaries[1].Address;
        RecapHistoryLoadMeasurement suffix =
            RecapHistoryLoadProjector.Measure(
                window,
                split,
                new ContentEstimator()
            );
        long prefix = whole.ReplaySafeBoundaries[1]
            .AbsorbedSinceBaseline.Value;

        Assert.Equal(
            whole.Growth.Value,
            checked(prefix + suffix.Growth.Value)
        );
    }

    [Fact]
    public void OutsideAndOutOfRangeBaselinesFailTyped() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        HistoryLoadMeasurementException outside =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                RecapHistoryLoadProjector.Measure(
                    window,
                    Address(99),
                    new ContentEstimator()
                )
            );
        SJ.SessionHistoryPlanningBoundary[] boundaries = [
            .. window.ReplaySafeBoundaries
        ];
        boundaries[1] = boundaries[1] with {
            CompletedUnitCount = window.Units.Count + 1
        };
        SJ.SessionHistoryPlanningWindow malformed =
            window with {
                ReplaySafeBoundaries = boundaries
            };
        HistoryLoadMeasurementException outOfRange =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                RecapHistoryLoadProjector.Measure(
                    malformed,
                    boundaries[1].Address,
                    new ContentEstimator()
                )
            );

        Assert.Equal(
            HistoryLoadMeasurementDefectCodes
                .CadenceBaselineInvalid,
            outside.Code
        );
        Assert.Equal(
            HistoryLoadMeasurementDefectCodes
                .CadenceBaselineInvalid,
            outOfRange.Code
        );
    }

    [Fact]
    public void ProjectorPassesExactUnitCapAndRejectsZeroLoad() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        var observing = new ContentEstimator();
        _ = RecapHistoryLoadProjector.Measure(
            window,
            window.StartExclusive,
            observing
        );
        Assert.All(
            observing.ObservedCaps,
            cap => Assert.Equal(
                HistoryLoadMeasurementSafety.V1
                    .MaxRenderedHistoryUnitUtf8Bytes,
                cap
            )
        );

        HistoryLoadMeasurementException failure =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                RecapHistoryLoadProjector.Measure(
                    window,
                    window.StartExclusive,
                    new DelegateEstimator(static _ =>
                        new HistoryUnitLoadMeasurement(
                            new HistoryLoadUnit(0),
                            1
                        )
                    )
                )
            );
        Assert.Equal(
            HistoryLoadMeasurementDefectCodes.MeasurementInvalid,
            failure.Code
        );
    }

    [Fact]
    public void CheckedLoadAggregationOverflowFailsTyped() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        var estimator = new DelegateEstimator(static _ =>
            new HistoryUnitLoadMeasurement(
                new HistoryLoadUnit(long.MaxValue),
                1
            )
        );

        HistoryLoadMeasurementException failure =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                RecapHistoryLoadProjector.Measure(
                    window,
                    window.StartExclusive,
                    estimator
                )
            );

        Assert.Equal(
            HistoryLoadMeasurementDefectCodes.MeasurementOverflow,
            failure.Code
        );
    }

    [Fact]
    public void WindowByteCapAcceptsExactAndRejectsOneMoreUnit() {
        int unitBytes =
            HistoryLoadMeasurementSafety.V1
                .MaxRenderedHistoryUnitUtf8Bytes;
        var estimator = new DelegateEstimator(_ =>
            new HistoryUnitLoadMeasurement(
                new HistoryLoadUnit(1),
                unitBytes
            )
        );
        SJ.SessionHistoryPlanningWindow exact =
            LinearWindow(unitCount: 8);
        SJ.SessionHistoryPlanningWindow oversized =
            LinearWindow(unitCount: 9);

        RecapHistoryLoadMeasurement accepted =
            RecapHistoryLoadProjector.Measure(
                exact,
                exact.StartExclusive,
                estimator
            );
        HistoryLoadMeasurementException failure =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                RecapHistoryLoadProjector.Measure(
                    oversized,
                    oversized.StartExclusive,
                    estimator
                )
            );

        Assert.Equal(8, accepted.Growth.Value);
        Assert.Equal(
            HistoryLoadMeasurementDefectCodes
                .HistoryLoadInputTooLarge,
            failure.Code
        );
    }

    [Fact]
    public void EstimatorFailureAndMalformedOutputAreTyped() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        HistoryLoadMeasurementException thrown =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                RecapHistoryLoadProjector.Measure(
                    window,
                    window.StartExclusive,
                    new DelegateEstimator(static _ =>
                        throw new InvalidOperationException("boom")
                    )
                )
            );
        HistoryLoadMeasurementException invalid =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                RecapHistoryLoadProjector.Measure(
                    window,
                    window.StartExclusive,
                    new DelegateEstimator(static _ =>
                        new HistoryUnitLoadMeasurement(
                            new HistoryLoadUnit(1),
                            -1
                        )
                    )
                )
            );

        Assert.Equal(
            HistoryLoadMeasurementDefectCodes.EstimatorFailed,
            thrown.Code
        );
        Assert.Equal(
            HistoryLoadMeasurementDefectCodes.MeasurementInvalid,
            invalid.Code
        );
    }

    [Fact]
    public void NullReplaySafeBoundaryFailsTypedBeforeBaselineLookup() {
        SJ.SessionHistoryPlanningWindow window = StandardWindow();
        SJ.SessionHistoryPlanningBoundary[] boundaries = [
            .. window.ReplaySafeBoundaries
        ];
        boundaries[0] = null!;

        HistoryLoadMeasurementException failure =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                RecapHistoryLoadProjector.Measure(
                    window with {
                        ReplaySafeBoundaries = boundaries
                    },
                    window.ReplaySafeBoundaries[1].Address,
                    new ContentEstimator()
                )
            );

        Assert.Equal(
            HistoryLoadMeasurementDefectCodes
                .PlanningWindowInvalid,
            failure.Code
        );
    }

    private static SJ.SessionHistoryPlanningWindow StandardWindow() {
        EventAddress start = Address(1);
        EventAddress[] raw = [
            Address(2),
            Address(3),
            Address(4),
            Address(5),
            Address(6),
            Address(7)
        ];
        SJ.SessionHistoryPlanningUnit[] units = [
            Unit("2", raw[0]),
            Unit("3", raw[2]),
            Unit("5", raw[4])
        ];
        SJ.SessionHistoryPlanningBoundary[] boundaries = [
            new(raw[0], 1),
            new(raw[1], 1),
            new(raw[2], 2),
            new(raw[3], 2),
            new(raw[4], 3),
            new(raw[5], 3)
        ];
        return Window(start, raw, units, boundaries);
    }

    private static SJ.SessionHistoryPlanningWindow LinearWindow(
        int unitCount
    ) {
        EventAddress start = Address(100);
        EventAddress[] raw = [
            .. Enumerable.Range(1, unitCount)
                .Select(index => Address((ulong)(100 + index)))
        ];
        SJ.SessionHistoryPlanningUnit[] units = [
            .. raw.Select((address, index) =>
                Unit((index + 1).ToString(), address)
            )
        ];
        SJ.SessionHistoryPlanningBoundary[] boundaries = [
            .. raw.Select((address, index) =>
                new SJ.SessionHistoryPlanningBoundary(
                    address,
                    index + 1
                )
            )
        ];
        return Window(start, raw, units, boundaries);
    }

    private static SJ.SessionHistoryPlanningWindow Window(
        EventAddress start,
        IReadOnlyList<EventAddress> raw,
        IReadOnlyList<SJ.SessionHistoryPlanningUnit> units,
        IReadOnlyList<SJ.SessionHistoryPlanningBoundary> boundaries
    ) {
        SJ.SessionContextAnchorSetupReferences setups = Setups(start);
        return new SJ.SessionHistoryPlanningWindow(
            raw[^1],
            start,
            setups,
            setups,
            raw,
            units,
            boundaries,
            new Dictionary<
                EventAddress,
                SJ.SessionContextAnchorSetupReferences
            >(),
            new SJ.SessionHistoryPlanningDiagnostics(0, 0, 0, 0)
        );
    }

    private static SJ.SessionContextAnchorSetupReferences Setups(
        EventAddress address
    ) => new(
        new SJ.SessionContextSetupReference(address, 1, new('a', 64)),
        new SJ.SessionContextSetupReference(address, 1, new('b', 64))
    );

    private static SJ.SessionHistoryPlanningUnit Unit(
        string content,
        EventAddress address
    ) => new(
        new ObservationMessage(content),
        address,
        address
    );

    private static EventAddress Address(ulong value)
        => new(
            SizedPtr.FromPacked(value),
            1,
            AddressHint.None
        );

    private sealed class ContentEstimator : IHistoryUnitLoadEstimator {
        internal const string IdValue = "test.content-load.v1";
        private readonly string? _failContent;

        internal ContentEstimator(string? failContent = null) {
            _failContent = failContent;
        }

        public string Id => IdValue;
        public int CallCount { get; private set; }
        public List<int> ObservedCaps { get; } = [];

        public HistoryUnitLoadMeasurement Measure(
            SJ.SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) {
            CallCount++;
            ObservedCaps.Add(maxRenderedUtf8Bytes);
            string content = Assert
                .IsType<ObservationMessage>(unit.Message)
                .Content!;
            if (string.Equals(
                    content,
                    _failContent,
                    StringComparison.Ordinal
                )) {
                throw new InvalidOperationException(
                    "Old prefix must not be measured."
                );
            }
            return new HistoryUnitLoadMeasurement(
                new HistoryLoadUnit(long.Parse(content)),
                1
            );
        }
    }

    private sealed class DelegateEstimator(
        Func<
            SJ.SessionHistoryPlanningUnit,
            HistoryUnitLoadMeasurement
        > measure
    ) : IHistoryUnitLoadEstimator {
        public string Id => "test.delegate-load.v1";

        public HistoryUnitLoadMeasurement Measure(
            SJ.SessionHistoryPlanningUnit unit,
            int maxRenderedUtf8Bytes
        ) => measure(unit);
    }
}
