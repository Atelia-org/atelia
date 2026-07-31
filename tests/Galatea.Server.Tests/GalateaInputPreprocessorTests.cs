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
        Assert.Equal("input-normalization-finish", turn.Phase);
        AssertSsePhases(
            Replay(turn),
            "input-normalization-start",
            "input-normalization-finish"
        );
        Assert.Contains(
            "\"changed\":true",
            JsonSerializer.Serialize(Replay(turn)[1].Payload),
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
        IReadOnlyList<StreamEventDto> events = Replay(turn);
        AssertSsePhases(
            events,
            "input-normalization-start",
            "input-normalization-finish"
        );
        string payload = JsonSerializer.Serialize(events[1].Payload);
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
    public async Task ProcessAsync_NormalizeExceptionFallsBackAndMarksSse() {
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
        IReadOnlyList<StreamEventDto> events = Replay(turn);
        AssertSsePhases(
            events,
            "input-normalization-start",
            "input-normalization-finish"
        );
        string payload = JsonSerializer.Serialize(events[1].Payload);
        Assert.Contains(
            "\"changed\":false",
            payload,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "\"fallback\":true",
            payload,
            StringComparison.Ordinal
        );
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
        Assert.Equal("input-normalization-start", turn.Phase);
        AssertSsePhases(
            Replay(turn),
            "input-normalization-start"
        );
    }

    private static GalateaLiveTurn Turn(string message) => new(
        message,
        new GalateaTurnOptions("test")
    );

    private static IReadOnlyList<StreamEventDto> Replay(
        GalateaLiveTurn turn
    ) {
        using GalateaTurnSubscription subscription = turn.Subscribe();
        return subscription.ReplayEvents;
    }

    private static void AssertSsePhases(
        IReadOnlyList<StreamEventDto> events,
        params string[] expected
    ) {
        Assert.Equal(expected.Length, events.Count);
        Assert.All(events, static item => Assert.Equal("meta", item.Type));
        Assert.Equal(
            expected,
            events.Select(static item =>
                JsonDocument.Parse(
                    JsonSerializer.Serialize(item.Payload)
                ).RootElement.GetProperty("phase").GetString()
            ).ToArray()
        );
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
