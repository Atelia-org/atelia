using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.Data;
using Atelia.EventJournal;
using Xunit;
using SJ = Atelia.SessionJournal;

namespace Atelia.SessionJournal.HistoryTimeline.Tests;

public sealed class O200kBaseHistoryUnitLoadEstimatorTests {
    private const int ExpectedMaxRenderedHistoryUnitUtf8Bytes =
        4 * 1024 * 1024;

    private readonly O200kBaseHistoryUnitLoadEstimator _estimator =
        new();

    [Theory]
    [InlineData("Hello, world!", 8)]
    [InlineData("你好，Galatea 👋", 11)]
    [InlineData("<|endoftext|>", 6)]
    public void ObservationGoldenIsStable(
        string content,
        long expectedLoad
    ) {
        SJ.SessionHistoryPlanningUnit unit = Unit(
            new ObservationMessage(content),
            10,
            11
        );

        HistoryUnitLoadMeasurement first = _estimator.Measure(
            unit,
            ExpectedMaxRenderedHistoryUnitUtf8Bytes
        );
        HistoryUnitLoadMeasurement second = _estimator.Measure(
            Unit(new ObservationMessage(content), 90, 99),
            ExpectedMaxRenderedHistoryUnitUtf8Bytes
        );

        Assert.Equal(
            O200kBaseHistoryUnitLoadEstimator.EstimatorId,
            _estimator.Id
        );
        Assert.Equal(expectedLoad, first.Load.Value);
        Assert.Equal(first, second);
        Assert.Equal(
            Encoding.UTF8.GetByteCount(
                $"[observation]\n{content}\n"
            ),
            first.RenderedUtf8Bytes
        );
    }

    [Fact]
    public void ActionFramingIsCanonicalAndReasoningOpaque() {
        var message = new ActionMessage([
            new ActionBlock.Text("answer"),
            new ActionBlock.ToolCall(
                new RawToolCall("lookup", "call-secret", "{\"q\":1}")
            ),
            new ActionBlock.TextReasoningBlock(
                "private chain",
                Descriptor("provider-a")
            )
        ]);

        HistoryUnitLoadRendering rendered =
            HistoryUnitLoadRenderer.Render(message, 4096);

        Assert.Equal(
            "[action]\n"
            + "[action-text]\nanswer\n"
            + "[tool-call-name]\nlookup\n"
            + "[tool-call-arguments]\n{\"q\":1}\n"
            + "[reasoning-opaque]\n",
            rendered.Text
        );
        Assert.Equal(
            Encoding.UTF8.GetByteCount(rendered.Text),
            rendered.Utf8Bytes
        );
        Assert.Equal(31, Measure(message).Load.Value);
    }

    [Fact]
    public void OpaqueReasoningAndToolCallIdDoNotAffectLoad() {
        ActionMessage first = ActionWithOpaqueFields(
            "call-1",
            "first private reasoning"
        );
        ActionMessage second = ActionWithOpaqueFields(
            "call-2",
            "completely different private reasoning"
        );

        Assert.Equal(
            Measure(first),
            Measure(second)
        );
        Assert.Equal(
            HistoryUnitLoadRenderer.Render(first, 4096),
            HistoryUnitLoadRenderer.Render(second, 4096)
        );
    }

    [Fact]
    public void ToolResultsFramingIncludesStatusAndTextButNotCallId() {
        var message = new ToolResultsMessage(
            "tool observation",
            [
                Tool(
                    "alpha",
                    "ignored-1",
                    ToolExecutionStatus.Success,
                    "A"
                ),
                Tool(
                    "beta",
                    "ignored-2",
                    ToolExecutionStatus.Failed,
                    "B"
                ),
                Tool(
                    "gamma",
                    "ignored-3",
                    ToolExecutionStatus.Skipped,
                    "C"
                )
            ]
        );

        HistoryUnitLoadRendering rendered =
            HistoryUnitLoadRenderer.Render(message, 4096);

        Assert.Equal(
            "[tool-results-content]\ntool observation\n"
            + "[tool-result-name]\nalpha\n"
            + "[tool-result-status]\nsuccess\n"
            + "[tool-result-text]\nA\n"
            + "[tool-result-name]\nbeta\n"
            + "[tool-result-status]\nfailed\n"
            + "[tool-result-text]\nB\n"
            + "[tool-result-name]\ngamma\n"
            + "[tool-result-status]\nskipped\n"
            + "[tool-result-text]\nC\n",
            rendered.Text
        );
        Assert.Equal(72, Measure(message).Load.Value);
        var sameBodyWithDifferentIds = new ToolResultsMessage(
            message.Content,
            [
                Tool("alpha", "other-1", ToolExecutionStatus.Success, "A"),
                Tool("beta", "other-2", ToolExecutionStatus.Failed, "B"),
                Tool("gamma", "other-3", ToolExecutionStatus.Skipped, "C")
            ]
        );
        Assert.Equal(Measure(message), Measure(sameBodyWithDifferentIds));
    }

    [Fact]
    public void EmptyContentStillHasPositiveLoad() {
        HistoryUnitLoadMeasurement measured =
            Measure(new ObservationMessage(null));

        Assert.True(measured.Load.Value >= 1);
        Assert.Equal(
            Encoding.UTF8.GetByteCount("[observation]\n\n"),
            measured.RenderedUtf8Bytes
        );
    }

    [Fact]
    public void InvalidUnicodeFailsTyped() {
        HistoryLoadMeasurementException failure =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                Measure(new ObservationMessage("\uD800"))
            );

        Assert.Equal(
            HistoryLoadMeasurementDefectCodes.InvalidUnicode,
            failure.Code
        );
    }

    [Fact]
    public void UnknownMessageFailsTyped() {
        HistoryLoadMeasurementException failure =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                Measure(new UnknownHistoryMessage())
            );

        Assert.Equal(
            HistoryLoadMeasurementDefectCodes
                .UnsupportedHistoryMessage,
            failure.Code
        );
    }

    [Fact]
    public void ExactUnitByteCapSucceedsAndOneLessFails() {
        var message = new ObservationMessage("é🙂");
        HistoryUnitLoadRendering rendered =
            HistoryUnitLoadRenderer.Render(message, 4096);

        HistoryUnitLoadMeasurement exact = _estimator.Measure(
            Unit(message),
            rendered.Utf8Bytes
        );
        HistoryLoadMeasurementException failure =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                _estimator.Measure(
                    Unit(message),
                    rendered.Utf8Bytes - 1
                )
            );

        Assert.Equal(rendered.Utf8Bytes, exact.RenderedUtf8Bytes);
        Assert.Equal(
            HistoryLoadMeasurementDefectCodes
                .HistoryLoadInputTooLarge,
            failure.Code
        );
    }

    [Fact]
    public void ConfiguredUnitSafetyCapRejectsOversizedContent() {
        string content = new(
            'a',
            ExpectedMaxRenderedHistoryUnitUtf8Bytes
        );

        HistoryLoadMeasurementException failure =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                Measure(new ObservationMessage(content))
            );

        Assert.Equal(
            HistoryLoadMeasurementDefectCodes
                .HistoryLoadInputTooLarge,
            failure.Code
        );
    }

    [Fact]
    public void InvalidArgumentsAndNegativeLoadAreRejected() {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _estimator.Measure(
                Unit(new ObservationMessage("x")),
                0
            )
        );
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HistoryLoadUnit(-1)
        );
    }

    [Fact]
    public void NullActionShapeAndInvalidToolStatusFailTyped() {
        ActionMessage nullBlock = new([
            null!
        ]);
        ActionMessage nullToolCall = new([
            new ActionBlock.ToolCall(null!)
        ]);
        var invalidStatus = new ToolResultsMessage(
            null,
            [
                Tool(
                    "tool",
                    "call",
                    (ToolExecutionStatus)99,
                    "result"
                )
            ]
        );

        AssertUnsupportedBlock(nullBlock);
        AssertUnsupportedBlock(nullToolCall);
        AssertUnsupportedBlock(invalidStatus);
    }

    private HistoryUnitLoadMeasurement Measure(
        IHistoryMessage message
    ) => _estimator.Measure(
        Unit(message),
        ExpectedMaxRenderedHistoryUnitUtf8Bytes
    );

    private void AssertUnsupportedBlock(IHistoryMessage message) {
        HistoryLoadMeasurementException failure =
            Assert.Throws<HistoryLoadMeasurementException>(() =>
                Measure(message)
            );
        Assert.Equal(
            HistoryLoadMeasurementDefectCodes
                .UnsupportedHistoryBlock,
            failure.Code
        );
    }

    private static ActionMessage ActionWithOpaqueFields(
        string toolCallId,
        string reasoning
    ) => new([
        new ActionBlock.ToolCall(
            new RawToolCall("lookup", toolCallId, "{}")
        ),
        new ActionBlock.TextReasoningBlock(
            reasoning,
            Descriptor("provider-a")
        )
    ]);

    private static ToolResult Tool(
        string name,
        string callId,
        ToolExecutionStatus status,
        string content
    ) => ToolResult.FromText(
        name,
        callId,
        status,
        content
    );

    private static CompletionDescriptor Descriptor(string provider)
        => new(provider, "test-api-v1", "model-a");

    private static SJ.SessionHistoryPlanningUnit Unit(
        IHistoryMessage message,
        ulong start = 10,
        ulong end = 11
    ) => new(message, Address(start), Address(end));

    private static EventAddress Address(ulong value)
        => new(
            SizedPtr.FromPacked(value),
            1,
            AddressHint.None
        );

    private sealed class UnknownHistoryMessage : IHistoryMessage {
        public HistoryMessageKind Kind =>
            HistoryMessageKind.Observation;
    }
}
