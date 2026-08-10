using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using static Atelia.SessionJournal.HistoryTimeline.Tests.HistoryTimelineTestData;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class HistoryPartitionerTests : IDisposable {
    private readonly List<string> _paths = [];

    [Fact]
    public void SelectsFirstReplaySafeBoundaryAndStopsMeasurementThere() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["2", "3", "999"],
            [(1, 1), (2, 1), (3, 2), (4, 2), (5, 3)]
        );
        var estimator = new DelegateEstimator(
            "atelia.tests.history-load.stop-v1",
            unit => {
                string content =
                    ((ObservationMessage)unit.Message).Content!;
                if (content == "999") {
                    throw new InvalidOperationException(
                        "terminal measurement was not respected"
                    );
                }
                return new HistoryUnitLoadMeasurement(
                    new HistoryLoadUnit(long.Parse(content)),
                    2
                );
            }
        );

        HistoryPartitionResult.Selected selected = Assert.IsType<
            HistoryPartitionResult.Selected
        >(HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(estimator.Id),
            estimator
        ));

        Assert.Equal(window.RawAddresses[2], selected.Point.EndInclusive);
        Assert.Equal(5, selected.Point.MeasuredHistoryLoad.Value);
        Assert.Equal(4, selected.Point.MeasuredRenderedUtf8Bytes);
        Assert.Equal(3, selected.Point.RawEventCount);
        Assert.Equal(2, selected.Point.EndCompletedUnitCount);
        Assert.Equal(2, estimator.CallCount);
    }

    [Fact]
    public void SharedCompletedCountSelectsRawEarliestLegalBoundary() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["5"],
            [(1, 1), (2, 1)]
        );
        DelegateEstimator estimator = NumericEstimator();

        HistoryPartitionResult.Selected selected = Assert.IsType<
            HistoryPartitionResult.Selected
        >(HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(estimator.Id),
            estimator
        ));

        Assert.Equal(window.RawAddresses[0], selected.Point.EndInclusive);
        Assert.Equal(1, estimator.CallCount);
    }

    [Fact]
    public void BMinusOneAtEvidenceEndReturnsNotEnough() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["4"],
            [(1, 1)],
            rawEventCount: 2
        );
        DelegateEstimator estimator = NumericEstimator();

        HistoryPartitionResult.NotEnough result = Assert.IsType<
            HistoryPartitionResult.NotEnough
        >(HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(estimator.Id),
            estimator
        ));

        Assert.Equal(4, result.MeasuredHistoryLoad.Value);
        Assert.Equal(2, result.RawEventCount);
        Assert.Equal(1, result.CompletedUnitCount);
    }

    [Fact]
    public void ExactRenderedByteCapCanStillSelectAtTarget() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["5"],
            [(1, 1)]
        );
        DelegateEstimator estimator = NumericEstimator(
            renderedBytes: 5
        );

        HistoryPartitionResult.Selected selected = Assert.IsType<
            HistoryPartitionResult.Selected
        >(HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(
                estimator.Id,
                target: 5,
                maxRenderedBytes: 5
            ),
            estimator
        ));

        Assert.Equal(5, selected.Point.MeasuredHistoryLoad.Value);
        Assert.Equal(5, selected.Point.MeasuredRenderedUtf8Bytes);
    }

    [Fact]
    public void ExactRenderedByteCapAtBMinusOneReturnsByteLimit() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["4"],
            [(1, 1)]
        );
        DelegateEstimator estimator = NumericEstimator(
            renderedBytes: 5
        );

        HistoryPartitionResult.LimitExceeded result = Assert.IsType<
            HistoryPartitionResult.LimitExceeded
        >(HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(
                estimator.Id,
                target: 5,
                maxRenderedBytes: 5
            ),
            estimator
        ));

        Assert.Equal(
            HistoryPartitionLimitKind.MaxRenderedBytes,
            result.Limit
        );
        Assert.Equal(4, result.MeasuredHistoryLoad.Value);
        Assert.Equal(5, result.MeasuredRenderedUtf8Bytes);
    }

    [Fact]
    public void ZeroRenderedByteEvidenceIsInvalidForPartition() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["5"],
            [(1, 1)]
        );
        DelegateEstimator estimator = NumericEstimator(
            renderedBytes: 0
        );

        HistoryLoadMeasurementException invalid = Assert.Throws<
            HistoryLoadMeasurementException
        >(() => HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(estimator.Id),
            estimator
        ));

        Assert.Equal(
            HistoryLoadMeasurementDefectCodes.MeasurementInvalid,
            invalid.Code
        );
    }

    [Fact]
    public void SelectedDoesNotObserveMalformedOrThrowingTailEvidence() {
        SJ.SessionHistoryPlanningWindow source = Window(
            ["5", "999"],
            [(1, 1), (2, 2)]
        );
        EventAddress selectedAddress = source.RawAddresses[0];
        var window = source with {
            RawAddresses = new ThrowingReadOnlyList<EventAddress>(
                [source.RawAddresses[0], default],
                accessibleCount: 1
            ),
            Units = new ThrowingReadOnlyList<
                SJ.SessionHistoryPlanningUnit
            >(
                [source.Units[0], null!],
                accessibleCount: 1
            ),
            ReplaySafeBoundaries = new ThrowingReadOnlyList<
                SJ.SessionHistoryPlanningBoundary
            >(
                [source.ReplaySafeBoundaries[0], null!],
                accessibleCount: 1
            ),
            ReplaySafeBoundarySetups = new ThrowingSetupMap(
                selectedAddress,
                source.ReplaySafeBoundarySetups[selectedAddress]
            )
        };
        DelegateEstimator estimator = NumericEstimator();

        HistoryPartitionResult.Selected selected = Assert.IsType<
            HistoryPartitionResult.Selected
        >(HistoryPartitioner.Partition(
            window,
            StartBaseline(window),
            Policy(estimator.Id),
            estimator
        ));

        Assert.Equal(selectedAddress, selected.Point.EndInclusive);
        Assert.Equal(1, estimator.CallCount);
    }

    [Fact]
    public void RawLimitDoesNotObserveAnyLaterEvidence() {
        SJ.SessionHistoryPlanningWindow source = Window(
            ["1", "999"],
            [(1, 1), (2, 2)]
        );
        SJ.SessionHistoryPlanningWindow window = WithThrowingTail(
            source,
            accessibleRawCount: 1,
            accessibleUnitCount: 1,
            accessibleBoundaryCount: 1
        );
        DelegateEstimator estimator = NumericEstimator();

        HistoryPartitionResult.LimitExceeded limited = Assert.IsType<
            HistoryPartitionResult.LimitExceeded
        >(HistoryPartitioner.Partition(
            window,
            StartBaseline(window),
            Policy(
                estimator.Id,
                target: 5,
                maxRawEvents: 1
            ),
            estimator
        ));

        Assert.Equal(
            HistoryPartitionLimitKind.MaxRawEvents,
            limited.Limit
        );
        Assert.Equal(1, estimator.CallCount);
    }

    [Fact]
    public void ByteLimitDoesNotObserveRemainingUnitsOrTailEvidence() {
        SJ.SessionHistoryPlanningWindow source = Window(
            ["1", "999"],
            [(1, 2), (2, 2)],
            rawEventCount: 2
        );
        SJ.SessionHistoryPlanningWindow window = WithThrowingTail(
            source,
            accessibleRawCount: 1,
            accessibleUnitCount: 1,
            accessibleBoundaryCount: 1
        );
        DelegateEstimator estimator = NumericEstimator(
            renderedBytes: 2
        );

        HistoryPartitionResult.LimitExceeded limited = Assert.IsType<
            HistoryPartitionResult.LimitExceeded
        >(HistoryPartitioner.Partition(
            window,
            StartBaseline(window),
            Policy(
                estimator.Id,
                target: 5,
                maxRenderedBytes: 1
            ),
            estimator
        ));

        Assert.Equal(
            HistoryPartitionLimitKind.MaxRenderedBytes,
            limited.Limit
        );
        Assert.Equal(1, estimator.CallCount);
    }

    [Fact]
    public void ExactRawCeilingSelectsButBelowTargetReturnsTypedLimit() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["2", "3"],
            [(1, 1), (3, 2)]
        );
        DelegateEstimator selectedEstimator = NumericEstimator();
        HistoryPartitionResult.Selected selected = Assert.IsType<
            HistoryPartitionResult.Selected
        >(HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(
                selectedEstimator.Id,
                maxRawEvents: 3
            ),
            selectedEstimator
        ));
        Assert.Equal(3, selected.Point.RawEventCount);

        DelegateEstimator limitedEstimator = NumericEstimator();
        HistoryPartitionResult.LimitExceeded limited = Assert.IsType<
            HistoryPartitionResult.LimitExceeded
        >(HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(
                limitedEstimator.Id,
                target: 6,
                maxRawEvents: 3
            ),
            limitedEstimator
        ));
        Assert.Equal(
            HistoryPartitionLimitKind.MaxRawEvents,
            limited.Limit
        );
        Assert.Equal(3, limited.RawEventCount);
    }

    [Fact]
    public void RenderedByteCeilingIsCheckedAtEveryCompletedUnit() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["2", "3", "8"],
            [(1, 1), (3, 3)]
        );
        DelegateEstimator estimator = NumericEstimator(
            renderedBytes: 4
        );

        HistoryPartitionResult.LimitExceeded limited = Assert.IsType<
            HistoryPartitionResult.LimitExceeded
        >(HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(
                estimator.Id,
                target: 20,
                maxRenderedBytes: 7
            ),
            estimator
        ));

        Assert.Equal(
            HistoryPartitionLimitKind.MaxRenderedBytes,
            limited.Limit
        );
        Assert.Equal(8, limited.MeasuredRenderedUtf8Bytes);
        Assert.Equal(2, limited.CompletedUnitCount);
        Assert.Equal(2, estimator.CallCount);
    }

    [Fact]
    public void EstimatorIdentityMismatchAndNonFatalFailureAreTyped() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["5"],
            [(1, 1)]
        );
        DelegateEstimator estimator = NumericEstimator();
        HistoryLoadMeasurementException mismatch =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                HistoryPartitioner.Partition(
                    window,
                    Baseline(window),
                    Policy("atelia.tests.other-estimator-v1"),
                    estimator
                )
            );
        Assert.Equal(
            HistoryLoadMeasurementDefectCodes.MeasurementInvalid,
            mismatch.Code
        );

        var failing = new DelegateEstimator(
            estimator.Id,
            _ => throw new InvalidOperationException("boom")
        );
        HistoryLoadMeasurementException wrapped =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                HistoryPartitioner.Partition(
                    window,
                    Baseline(window),
                    Policy(failing.Id),
                    failing
                )
            );
        Assert.Equal(
            HistoryLoadMeasurementDefectCodes.EstimatorFailed,
            wrapped.Code
        );
        Assert.IsType<InvalidOperationException>(wrapped.InnerException);
    }

    [Fact]
    public void FatalEstimatorExceptionPropagatesUnchanged() {
        SJ.SessionHistoryPlanningWindow window = Window(
            ["5"],
            [(1, 1)]
        );
        var fatal = new OutOfMemoryException("fatal");
        var estimator = new DelegateEstimator(
            "atelia.tests.history-load.fatal-v1",
            _ => throw fatal
        );

        OutOfMemoryException thrown = Assert.Throws<
            OutOfMemoryException
        >(() => HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(estimator.Id),
            estimator
        ));

        Assert.Same(fatal, thrown);
    }

    [Fact]
    public void RealToolCallResultFixtureCannotCutAtPartialResults() {
        string path = NewPath();
        EventAddress firstResult;
        EventAddress finalResult;
        using (EventJournal.EventJournal journal =
               EventJournal.EventJournal.CreateNew(path)) {
            journal.CreateBranch(
                SJ.SessionJournalDefaults.MainBranchName,
                startPoint: null
            ).Unwrap();
            EventAddress runtime = Commit(
                journal,
                null,
                SJ.SessionEventKind.RuntimeConfigSetup,
                new SJ.SessionRuntimeConfiguration(
                    "model-A",
                    "surface-A",
                    SJ.SessionJournalDefaults.Schema,
                    new(0)
                )
            );
            EventAddress prompt = Commit(
                journal,
                runtime,
                SJ.SessionEventKind.SystemPromptSetup,
                new SJ.SystemPromptSetupBody("system-A")
            );
            EventAddress created = Commit(
                journal,
                prompt,
                SJ.SessionEventKind.SessionCreated,
                new SJ.SessionCreatedBody(
                    SJ.SessionCreationOrigin.Native
                )
            );
            EventAddress observation = Commit(
                journal,
                created,
                SJ.SessionEventKind.ObservationAccepted,
                new SJ.ObservationAcceptedBody("use two tools")
            );
            string correlation =
                $"atelia.session-journal.turn.v1:{SJ.EventAddressTextCodec.Format(observation)}";
            var identity = new SJ.SessionToolRuntimeIdentity(
                "host",
                "implementations",
                "capabilities"
            );
            var action = new ActionMessage([
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-1", "{}")
                ),
                new ActionBlock.ToolCall(
                    new RawToolCall("lookup", "call-2", "{}")
                )
            ]);
            EventAddress imported = Commit(
                journal,
                observation,
                SJ.SessionEventKind.ImportedAgentAction,
                new SJ.AgentActionProducedBody(
                    action,
                    new CompletionDescriptor(
                        "import",
                        "import-v1",
                        "model-A"
                    ),
                    correlation,
                    new SJ.SessionExecutionCheckpoint(0),
                    identity
                )
            );
            EventAddress firstStarted = Commit(
                journal,
                imported,
                SJ.SessionEventKind.ToolExecutionStarted,
                new SJ.ToolExecutionStartedBody(
                    "call-1",
                    "lookup",
                    "{}",
                    "operation-1",
                    1,
                    identity
                )
            );
            firstResult = Commit(
                journal,
                firstStarted,
                SJ.SessionEventKind.ToolResultObserved,
                new SJ.ToolResultObservedBody(
                    "call-1",
                    "lookup",
                    1,
                    ToolExecutionStatus.Failed,
                    [new ToolResultBlock.Text("one")]
                )
            );
            EventAddress secondStarted = Commit(
                journal,
                firstResult,
                SJ.SessionEventKind.ToolExecutionStarted,
                new SJ.ToolExecutionStartedBody(
                    "call-2",
                    "lookup",
                    "{}",
                    "operation-2",
                    2,
                    identity
                )
            );
            finalResult = Commit(
                journal,
                secondStarted,
                SJ.SessionEventKind.ToolResultObserved,
                new SJ.ToolResultObservedBody(
                    "call-2",
                    "lookup",
                    2,
                    ToolExecutionStatus.Success,
                    [new ToolResultBlock.Text("two")]
                )
            );
        }

        using var engine = SJ.SessionJournalEngine.Open(path);
        SJ.SessionHistoryPlanningWindow window =
            engine.ReadHistoryPlanningWindow();
        var estimator = new DelegateEstimator(
            "atelia.tests.history-load.real-tool-v1",
            _ => new HistoryUnitLoadMeasurement(
                new HistoryLoadUnit(1),
                1
            )
        );

        HistoryPartitionResult.Selected selected = Assert.IsType<
            HistoryPartitionResult.Selected
        >(HistoryPartitioner.Partition(
            window,
            Baseline(window),
            Policy(estimator.Id, target: 3),
            estimator
        ));

        Assert.DoesNotContain(
            window.ReplaySafeBoundaries,
            boundary => boundary.Address == firstResult
        );
        Assert.Equal(finalResult, selected.Point.EndInclusive);
        Assert.Equal(3, selected.Point.EndCompletedUnitCount);
    }

    public void Dispose() {
        foreach (string path in _paths) {
            try {
                if (Directory.Exists(path)) {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch {
                // Best-effort cleanup.
            }
        }
    }

    private string NewPath() {
        string root = Directory.Exists("/dev/shm")
            ? "/dev/shm"
            : Path.GetTempPath();
        string path = Path.Combine(
            root,
            "atelia-history-timeline-tests",
            Guid.NewGuid().ToString("N")
        );
        _paths.Add(path);
        return path;
    }

    private static EventAddress Commit(
        EventJournal.EventJournal journal,
        EventAddress? expectedParent,
        SJ.SessionEventKind kind,
        object body
    ) => journal.CommitToRef(
        SJ.SessionJournalDefaults.MainBranchName,
        expectedParent,
        SJ.SessionEventCodec.Encode(kind, body),
        opaqueEventKind: (uint)kind,
        hint: default
    ).Unwrap().EventAddress;

    private static HistoryLoadBaseline StartBaseline(
        SJ.SessionHistoryPlanningWindow window
    ) => new(
        window.StartExclusive,
        completedUnitCount: 0,
        firstLaterBoundaryIndex: 0
    );

    private static SJ.SessionHistoryPlanningWindow WithThrowingTail(
        SJ.SessionHistoryPlanningWindow source,
        int accessibleRawCount,
        int accessibleUnitCount,
        int accessibleBoundaryCount
    ) => source with {
        RawAddresses = new ThrowingReadOnlyList<EventAddress>(
            source.RawAddresses,
            accessibleRawCount
        ),
        Units = new ThrowingReadOnlyList<
            SJ.SessionHistoryPlanningUnit
        >(source.Units, accessibleUnitCount),
        ReplaySafeBoundaries = new ThrowingReadOnlyList<
            SJ.SessionHistoryPlanningBoundary
        >(source.ReplaySafeBoundaries, accessibleBoundaryCount),
        ReplaySafeBoundarySetups = new ThrowingSetupMap(
            source.RawAddresses[0],
            source.ReplaySafeBoundarySetups[source.RawAddresses[0]]
        )
    };

    private sealed class ThrowingReadOnlyList<T>(
        IReadOnlyList<T> source,
        int accessibleCount
    ) : IReadOnlyList<T> {
        public int Count => source.Count;

        public T this[int index] => index < accessibleCount
            ? source[index]
            : throw new InvalidOperationException(
                "Partitioner observed terminal tail evidence."
            );

        public IEnumerator<T> GetEnumerator() {
            for (int index = 0; index < Count; index++) {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private sealed class ThrowingSetupMap(
        EventAddress allowedAddress,
        SJ.SessionContextAnchorSetupReferences allowedSetups
    ) : IReadOnlyDictionary<
        EventAddress,
        SJ.SessionContextAnchorSetupReferences
    > {
        public int Count => 2;
        public IEnumerable<EventAddress> Keys
            => throw TailObserved();
        public IEnumerable<SJ.SessionContextAnchorSetupReferences> Values
            => throw TailObserved();
        public SJ.SessionContextAnchorSetupReferences this[
            EventAddress key
        ] => key == allowedAddress
            ? allowedSetups
            : throw TailObserved();

        public bool ContainsKey(EventAddress key)
            => key == allowedAddress
                ? true
                : throw TailObserved();

        public bool TryGetValue(
            EventAddress key,
            out SJ.SessionContextAnchorSetupReferences value
        ) {
            if (key != allowedAddress) {
                throw TailObserved();
            }
            value = allowedSetups;
            return true;
        }

        public IEnumerator<KeyValuePair<
            EventAddress,
            SJ.SessionContextAnchorSetupReferences
        >> GetEnumerator() => throw TailObserved();

        System.Collections.IEnumerator
            System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();

        private static InvalidOperationException TailObserved()
            => new("Partitioner observed terminal setup tail evidence.");
    }
}
