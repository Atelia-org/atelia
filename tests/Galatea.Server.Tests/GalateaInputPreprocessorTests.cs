using Atelia.Completion;
using Atelia.Galatea.Server;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaInputPreprocessorTests {
    [Fact]
    public async Task ProcessAsync_SkipReturnsOriginalWithoutNormalization() {
        var normalizer = new StubNormalizer {
            ShouldNormalizeHandler = static _ => false
        };
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            "original",
            CancellationToken.None
        );

        Assert.Equal("original", result);
        Assert.Equal(1, normalizer.ShouldNormalizeCallCount);
        Assert.Equal(0, normalizer.NormalizeCallCount);
    }

    [Fact]
    public async Task ProcessAsync_SuccessReturnsEffectiveText() {
        var normalizer = new StubNormalizer {
            NormalizeHandler = static (_, _) =>
                ValueTask.FromResult("normalized")
        };
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            "original",
            CancellationToken.None
        );

        Assert.Equal("normalized", result);
        Assert.Equal(1, normalizer.ShouldNormalizeCallCount);
        Assert.Equal(1, normalizer.NormalizeCallCount);
    }

    [Fact]
    public async Task ProcessAsync_BlankOutputFallsBackWithoutFailureFlag() {
        var normalizer = new StubNormalizer {
            NormalizeHandler = static (_, _) =>
                ValueTask.FromResult("   ")
        };
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            "original",
            CancellationToken.None
        );

        Assert.Equal("original", result);
        Assert.Equal(1, normalizer.NormalizeCallCount);
    }

    [Fact]
    public async Task ProcessAsync_NormalizeExceptionFallsBackToOriginal() {
        var normalizer = new StubNormalizer {
            NormalizeHandler = static (_, _) =>
                ValueTask.FromException<string>(
                    new InvalidOperationException("normalizer failed")
                )
        };
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            "original",
            CancellationToken.None
        );

        Assert.Equal("original", result);
        Assert.Equal(1, normalizer.NormalizeCallCount);
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
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        GalateaTurnException exception = await Assert.ThrowsAsync<
            GalateaTurnException
        >(() => preprocessor.ProcessAsync(
            "original",
            CancellationToken.None
        ).AsTask());

        Assert.Equal("input-limit-exceeded", exception.FailureReason);
    }

    [Fact]
    public async Task ProcessAsync_ShouldNormalizeExceptionFallsBackWithoutNormalization() {
        var normalizer = new StubNormalizer {
            ShouldNormalizeHandler = static _ => throw new(
                "policy failed"
            )
        };
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        string result = await preprocessor.ProcessAsync(
            "original",
            CancellationToken.None
        );

        Assert.Equal("original", result);
        Assert.Equal(0, normalizer.NormalizeCallCount);
    }

    [Fact]
    public async Task ProcessAsync_FatalNormalizerFailurePropagates() {
        var failure = new OutOfMemoryException("fatal normalizer failure");
        var normalizer = new StubNormalizer {
            ShouldNormalizeHandler = _ => throw failure
        };
        var preprocessor = new GalateaInputPreprocessor(normalizer);

        OutOfMemoryException observed = await Assert.ThrowsAsync<
            OutOfMemoryException>(() => preprocessor.ProcessAsync(
                "original",
                CancellationToken.None
            ).AsTask());

        Assert.Same(failure, observed);
        Assert.Equal(0, normalizer.NormalizeCallCount);
    }

    [Fact]
    public async Task ConfiguredNormalizer_DoesNotSwallowFatalClientCreation() {
        var failure = new OutOfMemoryException("fatal client creation");
        IGalateaUserMessageNormalizer normalizer =
            new GalateaUserMessageNormalizerFactory().Create(
                new CompletionConnectionConfig(
                    "helper",
                    "openai-chat",
                    "helper-model",
                    "openai-chat/strict",
                    "http://localhost:8000/"
                ),
                () => throw failure
            );

        OutOfMemoryException observed = await Assert.ThrowsAsync<
            OutOfMemoryException>(() => normalizer.NormalizeAsync(
                "short input",
                CancellationToken.None
            ).AsTask());

        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task ProcessAsync_CallerCancellationPropagates() {
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
        var preprocessor = new GalateaInputPreprocessor(normalizer);
        using var cancellation = new CancellationTokenSource();

        Task<string> processing = preprocessor
            .ProcessAsync("original", cancellation.Token)
            .AsTask();
        await entered.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processing
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
