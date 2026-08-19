using Atelia.SessionJournal.MemoPod;

namespace Atelia.SessionJournal.MemoPod.Tests.Prompt;

public sealed class MemoPodFrozenPromptTests {
    private static readonly MemoPodId PodId = MemoPodId.Parse(
        "0123456789abcdef0123456789abcdef"
    );

    [Fact]
    public void TokenEstimatorIsReplaceableWithoutChangingRenderIdentity() {
        MemoPodFrozenPrompt prompt = MemoPodPromptRenderer.Render(
            new MemoPodDocument(
                PodId,
                "topic",
                2,
                [new Memo(MemoId.FromOrdinal(1), "正文😀")]
            )
        );
        string exactText = prompt.ExactText;
        int utf8Length = prompt.Utf8Length;
        string sha256 = prompt.Sha256;
        var first = new RecordingEstimator(17);
        var second = new RecordingEstimator(29);

        Assert.Equal(17, prompt.EstimateTokenCount(first));
        Assert.Equal(29, prompt.EstimateTokenCount(second));

        Assert.Equal(prompt.ExactText, first.ObservedExactText);
        Assert.Equal(prompt.ExactText, second.ObservedExactText);
        Assert.Same(exactText, prompt.ExactText);
        Assert.Equal(utf8Length, prompt.Utf8Length);
        Assert.Equal(sha256, prompt.Sha256);
    }

    [Fact]
    public void TokenEstimatorMustBePresentAndReturnNonNegativeCount() {
        MemoPodFrozenPrompt prompt = MemoPodPromptRenderer.Render(
            new MemoPodDocument(PodId, "topic", 1, [])
        );

        Assert.Throws<ArgumentNullException>(() =>
            prompt.EstimateTokenCount(null!));
        Assert.Throws<InvalidOperationException>(() =>
            prompt.EstimateTokenCount(new RecordingEstimator(-1)));
    }

    private sealed class RecordingEstimator(int tokenCount)
        : IMemoPodPromptTokenEstimator {
        internal string? ObservedExactText { get; private set; }

        public int EstimateTokenCount(string exactPromptText) {
            ObservedExactText = exactPromptText;
            return tokenCount;
        }
    }
}
