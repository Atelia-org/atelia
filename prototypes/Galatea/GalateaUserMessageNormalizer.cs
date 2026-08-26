using System.Collections.Immutable;
using System.Security;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Atelia.Diagnostics;

namespace Atelia.Galatea.Server;

public interface IGalateaUserMessageNormalizer {
    bool ShouldNormalize(string userMessage);

    ValueTask<string> NormalizeAsync(string userMessage, CancellationToken ct);
}

public interface IGalateaUserMessageNormalizerFactory {
    IGalateaUserMessageNormalizer Create(
        CompletionConnectionConfig? connection,
        Func<ICompletionClient> getClient
    );
}

public sealed class DisabledGalateaUserMessageNormalizer : IGalateaUserMessageNormalizer {
    public static DisabledGalateaUserMessageNormalizer Instance { get; } = new();

    private DisabledGalateaUserMessageNormalizer() { }

    public bool ShouldNormalize(string userMessage) => false;

    public ValueTask<string> NormalizeAsync(string userMessage, CancellationToken ct) {
        return ValueTask.FromResult(userMessage ?? string.Empty);
    }
}

public sealed class GalateaUserMessageNormalizerFactory
    : IGalateaUserMessageNormalizerFactory {
    public IGalateaUserMessageNormalizer Create(
        CompletionConnectionConfig? connection,
        Func<ICompletionClient> getClient
    ) {
        ArgumentNullException.ThrowIfNull(getClient);
        return connection is null
            ? DisabledGalateaUserMessageNormalizer.Instance
            : new ConfiguredGalateaUserMessageNormalizer(
                connection,
                getClient
            );
    }
}

public sealed class ConfiguredGalateaUserMessageNormalizer
    : IGalateaUserMessageNormalizer {
    private const string DebugCategory = "Galatea.Input";
    private const int MaxMessageLengthChars = 280;
    private const int MaxMessageLines = 4;

    private const string NormalizerSystemPrompt = """
你是一个“玩家输入清洗器”。
你的唯一任务是对短用户消息做最小必要的纠错和排版清洗，然后返回最终文本。

严格规则：
1. 只修正明显错别字、误触字、缺失或多余空格、全半角和标点。
2. 不改变原意，不扩写，不总结，不解释，不补充设定。
3. 保留原有语气、口吻、角色扮演风格和信息量。
4. 专有名词、角色名、游戏术语、缩写、黑话，除非错误极其明显且改正后几乎无歧义，否则不要擅自改动。
5. 如果拿不准，就保持原文。
6. 输出时只返回一个 <cleaned>...</cleaned> XML 片段，不要输出任何额外说明、引号、markdown 或前后缀。
""";

    private readonly CompletionConnectionConfig _connection;
    private readonly Func<ICompletionClient> _getClient;

    public ConfiguredGalateaUserMessageNormalizer(
        CompletionConnectionConfig connection,
        Func<ICompletionClient> getClient
    ) {
        _connection = connection
            ?? throw new ArgumentNullException(nameof(connection));
        _getClient = getClient
            ?? throw new ArgumentNullException(nameof(getClient));

        DebugUtil.Info(
            DebugCategory,
            $"User input normalization enabled: connection={connection.Id}, model={connection.ModelId}, maxChars={MaxMessageLengthChars}, maxLines={MaxMessageLines}"
        );
    }

    public bool ShouldNormalize(string userMessage) {
        if (string.IsNullOrWhiteSpace(userMessage)) { return false; }
        if (userMessage.Length > MaxMessageLengthChars) { return false; }
        if (userMessage.Contains("```", StringComparison.Ordinal)) { return false; }
        return CountLines(userMessage) <= MaxMessageLines;
    }

    public async ValueTask<string> NormalizeAsync(string userMessage, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        if (!ShouldNormalize(userMessage)) { return userMessage; }

        try {
            var request = new CompletionRequest(
                _connection.ModelId,
                new CompletionPromptPrefix(
                    NormalizerSystemPrompt,
                    CompletionOutputContract.ProviderDefault(
                        ImmutableArray<ToolDefinition>.Empty
                    ),
                    [new ObservationMessage(BuildNormalizationPrompt(userMessage))]
                ),
                tailMessages: [],
                maxTokens: _connection.MaxTokens
            );

            ICompletionClient completionClient = _getClient();
            var result = await completionClient.StreamCompletionAsync(request, observer: null, cancellationToken: ct)
                .ConfigureAwait(false);

            if (!result.Termination.IsSuccess) {
                DebugUtil.Warning(
                    DebugCategory,
                    $"Input normalization fallback to original: termination={result.Termination.Kind}, providerReason={result.Termination.ProviderReason ?? "<none>"}, input={Preview(userMessage)}"
                );
                return userMessage;
            }

            string normalized = ExtractNormalizedText(result.Message.GetFlattenedText());
            if (string.IsNullOrWhiteSpace(normalized)) {
                DebugUtil.Warning(
                    DebugCategory,
                    $"Input normalization produced empty or invalid output; keeping original. input={Preview(userMessage)}"
                );
                return userMessage;
            }

            bool changed = !string.Equals(userMessage, normalized, StringComparison.Ordinal);
            if (changed) {
                DebugUtil.Info(
                    DebugCategory,
                    $"Input normalized: before={Preview(userMessage)}, after={Preview(normalized)}"
                );
            }
            else {
                DebugUtil.Trace(DebugCategory, $"Input normalization kept original: input={Preview(userMessage)}");
            }

            return normalized;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        }
        catch (Exception ex) when (
            GalateaExceptionClassifier.IsNonFatal(ex)) {
            DebugUtil.Warning(
                DebugCategory,
                $"Input normalization failed; keeping original. input={Preview(userMessage)}, error={ex.Message}",
                ex
            );
            return userMessage;
        }
    }

    internal static string BuildNormalizationPrompt(string userMessage) {
        return """
请清洗下面这条玩家输入。只做最小必要的修正，并且只返回一个 <cleaned>...</cleaned> XML 片段。

<player-input>
""" + EscapeXmlText(userMessage) + """
</player-input>
""";
    }

    internal static string ExtractNormalizedText(string rawText) {
        string stripped = InlineThinkTextFilter.StripInlineThinkBlocks(rawText).Trim();
        if (string.IsNullOrWhiteSpace(stripped)) { return string.Empty; }

        string? tagged = TryExtractTag(stripped, "cleaned");
        return string.IsNullOrWhiteSpace(tagged) ? string.Empty : tagged.Trim();
    }

    private static string? TryExtractTag(string text, string tagName) {
        string startTag = "<" + tagName + ">";
        string endTag = "</" + tagName + ">";

        int start = text.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0) { return null; }

        start += startTag.Length;
        int end = text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) { return null; }

        return text[start..end];
    }

    private static string EscapeXmlText(string text) {
        return SecurityElement.Escape(text) ?? string.Empty;
    }

    private static int CountLines(string text) {
        int lines = 1;
        for (int i = 0; i < text.Length; i++) {
            if (text[i] == '\n') { lines++; }
        }

        return lines;
    }

    private static string Preview(string? text) {
        if (string.IsNullOrWhiteSpace(text)) { return "<null>"; }
        string normalized = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= 120 ? normalized : normalized[..120] + "...";
    }
}
