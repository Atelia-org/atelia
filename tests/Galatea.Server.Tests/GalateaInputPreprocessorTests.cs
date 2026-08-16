using System.Text.Json;
using Atelia.Galatea.Server;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaInputPreprocessorTests {
    [Fact]
    public async Task ProcessAsync_SkipReturnsOriginalWithoutSsePhase() {
        var normalizer = new StubNormalizer {
            ShouldNormalizeHandler = static _ => false
        };
        GalateaLiveTurn turn = Turn("original");
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            turn,
            CancellationToken.None
        );

        Assert.Equal("original", result);
        Assert.Equal(1, normalizer.ShouldNormalizeCallCount);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Null(turn.Phase);
        Assert.Empty(Replay(turn));
    }

    [Fact]
    public async Task ProcessAsync_SuccessPublishesStartAndFinish() {
        var normalizer = new StubNormalizer {
            NormalizeHandler = static (_, _) =>
                ValueTask.FromResult("normalized")
        };
        GalateaLiveTurn turn = Turn("original");
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            turn,
            CancellationToken.None
        );

        Assert.Equal("normalized", result);
        Assert.Equal("input-normalization-finished", turn.Phase);
        AssertSseStatuses(
            Replay(turn),
            "normalizing-input",
            "input-normalization-finished"
        );
        Assert.Contains(
            "\"changed\":true",
            FrameText(Replay(turn)[1]),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ProcessAsync_BlankOutputFallsBackWithoutFailureFlag() {
        var normalizer = new StubNormalizer {
            NormalizeHandler = static (_, _) =>
                ValueTask.FromResult("   ")
        };
        GalateaLiveTurn turn = Turn("original");
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            turn,
            CancellationToken.None
        );

        Assert.Equal("original", result);
        IReadOnlyList<GalateaSseFrame> events = Replay(turn);
        AssertSseStatuses(
            events,
            "normalizing-input",
            "input-normalization-finished"
        );
        string payload = FrameText(events[1]);
        Assert.Contains(
            "\"changed\":false",
            payload,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "fallback",
            payload,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ProcessAsync_NormalizeExceptionFallsBackWithoutWireDetail() {
        var normalizer = new StubNormalizer {
            NormalizeHandler = static (_, _) =>
                ValueTask.FromException<string>(
                    new InvalidOperationException("normalizer failed")
                )
        };
        GalateaLiveTurn turn = Turn("original");
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            turn,
            CancellationToken.None
        );

        Assert.Equal("original", result);
        IReadOnlyList<GalateaSseFrame> events = Replay(turn);
        AssertSseStatuses(
            events,
            "normalizing-input",
            "input-normalization-finished"
        );
        string payload = FrameText(events[1]);
        Assert.Contains(
            "\"changed\":false",
            payload,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "fallback",
            payload,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task ProcessAsync_NormalizedMessageOver64KiBDoesNotFallback() {
        var normalizer = new StubNormalizer {
            NormalizeHandler = static (_, _) => ValueTask.FromResult(
                new string(
                    'x',
                    GalateaHttpV1.MaximumMessageUtf8Bytes + 1
                )
            )
        };
        GalateaLiveTurn turn = Turn("original");
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        GalateaTurnException exception = await Assert.ThrowsAsync<
            GalateaTurnException
        >(() => preprocessor.ProcessAsync(
            turn,
            CancellationToken.None
        ).AsTask());

        Assert.Equal("input-limit-exceeded", exception.FailureReason);
        AssertSseStatuses(Replay(turn), "normalizing-input");
    }

    [Fact]
    public async Task ProcessAsync_ShouldNormalizeExceptionFallsBackWithoutSse() {
        var normalizer = new StubNormalizer {
            ShouldNormalizeHandler = static _ => throw new(
                "policy failed"
            )
        };
        GalateaLiveTurn turn = Turn("original");
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            turn,
            CancellationToken.None
        );

        Assert.Equal("original", result);
        Assert.Equal(0, normalizer.NormalizeCallCount);
        Assert.Null(turn.Phase);
        Assert.Empty(Replay(turn));
    }

    [Fact]
    public async Task ProcessAsync_CallerCancellationPropagatesAndKeepsStartPhase() {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var normalizer = new StubNormalizer {
            NormalizeHandler = async (_, cancellationToken) => {
                entered.SetResult();
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken
                );
                return "unreachable";
            }
        };
        GalateaLiveTurn turn = Turn("original");
        var preprocessor = new GalateaInputPreprocessor(normalizer);
        using var cancellation = new CancellationTokenSource();

        Task<string> processing = preprocessor
            .ProcessAsync(turn, cancellation.Token)
            .AsTask();
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processing
        );
        Assert.Equal("normalizing-input", turn.Phase);
        AssertSseStatuses(
            Replay(turn),
            "normalizing-input"
        );
    }

    private static GalateaLiveTurn Turn(string message) => new(
        message,
        new GalateaTurnOptions("test")
    );

    private static IReadOnlyList<GalateaSseFrame> Replay(
        GalateaLiveTurn turn
    ) {
        using GalateaTurnSubscription subscription = turn.Subscribe();
        return subscription.ReplayFrames;
    }

    private static void AssertSseStatuses(
        IReadOnlyList<GalateaSseFrame> events,
        params string[] expected
    ) {
        Assert.Equal(expected.Length, events.Count);
        Assert.All(
            events,
            static item => Assert.Equal("status", item.EventName)
        );
        Assert.Equal(
            expected,
            events.Select(static item =>
                FramePayload(item).GetProperty("code").GetString()
            ).ToArray()
        );
    }

    private static string FrameText(GalateaSseFrame frame) =>
        System.Text.Encoding.UTF8.GetString(frame.Utf8.Span);

    private static JsonElement FramePayload(GalateaSseFrame frame) {
        string dataLine = FrameText(frame).Split('\n')[1];
        using JsonDocument document = JsonDocument.Parse(
            dataLine["data: ".Length..]
        );
        return document.RootElement.Clone();
    }

    private sealed class StubNormalizer
        : IGalateaUserMessageNormalizer {
        private int _shouldNormalizeCallCount;
        private int _normalizeCallCount;

        internal Func<string, bool> ShouldNormalizeHandler {
            get;
            init;
        } = static _ => true;

        internal Func<
            string,
            CancellationToken,
            ValueTask<string>
        > NormalizeHandler { get; init; } = static (message, _) =>
            ValueTask.FromResult(message);

        internal int ShouldNormalizeCallCount => Volatile.Read(
            ref _shouldNormalizeCallCount
        );

        internal int NormalizeCallCount => Volatile.Read(
            ref _normalizeCallCount
        );

        public bool ShouldNormalize(string userMessage) {
            Interlocked.Increment(ref _shouldNormalizeCallCount);
            return ShouldNormalizeHandler(userMessage);
        }

        public ValueTask<string> NormalizeAsync(
            string userMessage,
            CancellationToken ct
        ) {
            Interlocked.Increment(ref _normalizeCallCount);
            return NormalizeHandler(userMessage, ct);
        }
    }
}
