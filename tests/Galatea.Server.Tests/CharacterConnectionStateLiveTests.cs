using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Galatea.Prompts;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

/// <summary>
/// Opt-in semantic check against a cheap real model using synthetic Actions only.
/// No Galatea configuration, Journal, character session, or provider text is saved.
/// </summary>
public sealed class CharacterConnectionStateLiveTests {
    private const string EnableVariable =
        "ATELIA_RUN_GALATEA_CONNECTION_STATE_LIVE";

    [Theory]
    [InlineData("final-dress")]
    [InlineData("dress-then-pants")]
    [InlineData("planned-glasses")]
    [InlineData("explicitly-without-glasses")]
    [InlineData("glasses-unmentioned")]
    [InlineData("other-character-glasses")]
    [InlineData("summary-conflicts-with-narrative")]
    [Trait("Category", "GalateaConnectionStateLive")]
    public async Task DeepSeekFlashRecognizesFinalStateWithoutInventingMissingState(
        string sampleName
    ) {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1") {
            return;
        }

        string baseAddress = RequiredEnvironment("DEEPSEEK_BASE_URL");
        string apiKey = RequiredEnvironment("DEEPSEEK_API_KEY");
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")) {
            throw new InvalidOperationException("DEEPSEEK_BASE_URL must be an absolute HTTP URL.");
        }

        var connection = new CompletionConnectionConfig(
            "synthetic-connection-state-extractor",
            "openai-chat",
            "deepseek-v4-flash",
            "openai-chat/deepseek-v4",
            baseAddress,
            ApiKey: apiKey,
            ReasoningEffort: CompletionReasoningEffort.Disabled
        );
        ICompletionClient client = new DefaultCompletionClientFactory().Create(connection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try {
            (GalateaCharacterConnectionOption[] options, string action,
                string? expectedConnectionId) = Sample(sampleName);
            var extractor = new CharacterConnectionStateExtractor(
                new GalateaCharacterName("Galatea"), options,
                connection, () => client);
            CharacterConnectionStateMatch? actual = await extractor.ExtractAsync(
                action, deadline.Token);
            Assert.True(string.Equals(expectedConnectionId, actual?.ConnectionId,
                StringComparison.Ordinal),
                $"Synthetic sample '{sampleName}': expected connectionId '{expectedConnectionId ?? "null"}', actual '{actual?.ConnectionId ?? "null"}'.");
            if (actual is not null) {
                Assert.Contains(actual.Evidence, action, StringComparison.Ordinal);
            }
        }
        finally {
            (client as IDisposable)?.Dispose();
        }
    }

    private static (GalateaCharacterConnectionOption[] Options, string Action,
        string? ExpectedConnectionId) Sample(string name) => name switch {
        "final-dress" => (DressOptions(), """
[Galatea]
我换上蓝色裙装，坐回窗边。
[状态摘要]
Galatea当前穿着蓝色裙装。
""", "dress"),
        "dress-then-pants" => (DressOptions(), """
[Galatea]
我先穿上裙装，觉得行动不便，又换成深色裤装。
[状态摘要]
Galatea最终穿着深色裤装。
""", "pants"),
        "planned-glasses" => (GlassesOptions(), """
[Galatea]
我想过一会儿戴上眼镜，但现在先读这封信。
[状态摘要]
Galatea正在读信；眼镜佩戴情况未记录。
""", null),
        "explicitly-without-glasses" => (GlassesOptions(), """
[Galatea]
我摘下眼镜，把它放进衣袋。
[状态摘要]
Galatea当前未佩戴眼镜。
""", "without-glasses"),
        "glasses-unmentioned" => (GlassesOptions(), """
[Galatea]
我走到窗边，看见雨水沿玻璃滑落。
[状态摘要]
Galatea站在窗边，正在观察外面的雨。
""", null),
        "other-character-glasses" => (GlassesOptions(), """
[旁白]
Mira戴着眼镜，正在读书。Galatea坐在房间另一侧，听她说话。
[状态摘要]
Galatea正在听Mira说话；没有记录Galatea是否佩戴眼镜。
""", null),
        "summary-conflicts-with-narrative" => (DressOptions(), """
[Galatea]
我换上裙装后，又把裙装脱下，最终穿上裤装。
[状态摘要]
Galatea当前穿着裙装。
""", null),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for this opt-in test.");

    private static GalateaCharacterConnectionOption[] DressOptions() => [
        new("dress", "裙装", "Galatea回合结束时穿着裙装"),
        new("pants", "裤装", "Galatea回合结束时穿着裤装"),
    ];

    private static GalateaCharacterConnectionOption[] GlassesOptions() => [
        new("with-glasses", "佩戴眼镜", "Galatea回合结束时佩戴着眼镜"),
        new("without-glasses", "未佩戴眼镜", "Galatea回合结束时没有佩戴眼镜"),
    ];
}
