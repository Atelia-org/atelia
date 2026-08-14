using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Atelia.SessionJournal.RecapGrid.Manager;

namespace Atelia.SessionJournal.RecapGrid.Runtime;

internal abstract record RuntimeParseResult {
    private RuntimeParseResult() { }

    internal sealed record Parsed(
        RecapCellExecutionOutcome Outcome,
        int IgnoredPreTerminalTextBlockCount,
        int IgnoredPreTerminalTextUtf8Bytes
    ) : RuntimeParseResult;

    internal sealed record Failed(string Code, string Detail)
        : RuntimeParseResult;
}

internal static class RuntimeParser {
    internal const int MaximumNeutralContentUtf8Bytes = 256 * 1024;
    private const int MaximumArgumentsUtf8Bytes =
        (MaximumNeutralContentUtf8Bytes * 6) + 16 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static RuntimeParseResult Parse(
        PreparedRecapWork prepared,
        CompletionResult result
    ) {
        if (result is null) {
            return Failed(
                "CompletionResultNull",
                "The completion invoker returned null."
            );
        }
        if (!result.Termination.IsSuccess) {
            return Failed("CompletionIncomplete", "Completion did not terminate successfully.");
        }
        if (result.Errors is { Count: > 0 }) {
            return Failed("CompletionReportedErrors", "Completion reported provider errors.");
        }
        RecapCompletionRoute route = prepared.Route;
        if (!string.Equals(
                result.Invocation.ProviderId,
                route.Invoker.ProviderId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.Invocation.ApiSpecId,
                route.Invoker.ApiSpecId,
                StringComparison.Ordinal)
            || !string.Equals(
                result.Invocation.Model,
                route.ModelId,
                StringComparison.Ordinal)) {
            return Failed("InvocationMismatch", "Completion invocation differs from the selected route.");
        }
        ActionBlock.ToolCall? terminal = null;
        int ignoredTextBlockCount = 0;
        int ignoredTextUtf8Bytes = 0;
        foreach (ActionBlock output in result.Message.Blocks) {
            switch (output) {
                case ActionBlock.ReasoningBlock:
                    continue;
                case ActionBlock.Text text when terminal is null:
                    try {
                        ignoredTextUtf8Bytes = checked(
                            ignoredTextUtf8Bytes
                            + StrictUtf8.GetByteCount(text.Content)
                        );
                        ignoredTextBlockCount = checked(
                            ignoredTextBlockCount + 1
                        );
                    }
                    catch (Exception exception) when (exception is
                        EncoderFallbackException or OverflowException) {
                        return Failed(
                            "TerminalToolCallInvalid",
                            "Pre-terminal text is not valid bounded UTF-8 input."
                        );
                    }
                    continue;
                case ActionBlock.ToolCall toolCall when terminal is null:
                    terminal = toolCall;
                    continue;
                default:
                    return InvalidTerminalEnvelope();
            }
        }
        if (terminal is null
            || !string.Equals(
                terminal.Call.ToolName,
                prepared.Work.Family.OutputProtocol.TerminalToolName,
                StringComparison.Ordinal)) {
            return InvalidTerminalEnvelope();
        }
        ActionBlock.ToolCall block = terminal;
        string arguments = block.Call.RawArgumentsJson;
        if (string.IsNullOrEmpty(arguments)
            || arguments[0] == '\uFEFF') {
            return Failed("TerminalArgumentsInvalid", "Terminal arguments are empty or start with a BOM.");
        }
        int argumentBytes;
        try {
            argumentBytes = StrictUtf8.GetByteCount(arguments);
        }
        catch (EncoderFallbackException) {
            return Failed("TerminalArgumentsInvalidUtf16", "Terminal arguments contain invalid UTF-16.");
        }
        if (argumentBytes > MaximumArgumentsUtf8Bytes) {
            return Failed("TerminalArgumentsTooLarge", "Terminal arguments exceed the V2 bound.");
        }
        byte[] utf8 = new byte[argumentBytes];
        _ = StrictUtf8.GetBytes(arguments.AsSpan(), utf8);

        try {
            using JsonDocument document = JsonDocument.Parse(
                utf8,
                new JsonDocumentOptions {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8
                }
            );
            if (document.RootElement.ValueKind != JsonValueKind.Object) {
                return Failed("TerminalArgumentsInvalid", "Terminal arguments must be an object.");
            }
            JsonProperty[] properties = [.. document.RootElement.EnumerateObject()];
            if (properties.Length != 2
                || properties.Select(static property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != 2
                || !properties.Any(static property => property.NameEquals("outcome"))
                || !properties.Any(static property => property.NameEquals("content"))) {
                return Failed("TerminalArgumentsShapeInvalid", "Terminal arguments require exactly outcome and content once each.");
            }
            JsonElement outcomeElement = properties.Single(
                static property => property.NameEquals("outcome")
            ).Value;
            JsonElement contentElement = properties.Single(
                static property => property.NameEquals("content")
            ).Value;
            if (outcomeElement.ValueKind != JsonValueKind.String) {
                return Failed("TerminalOutcomeInvalid", "Terminal outcome must be a string.");
            }
            string? outcome = outcomeElement.GetString();
            if (string.Equals(
                    outcome,
                    RecapRewriterProtocolV2.KeepUnchangedOutcome,
                    StringComparison.Ordinal)) {
                if (contentElement.ValueKind != JsonValueKind.Null
                    || prepared.SameColumnPrior is null) {
                    return Failed("KeepUnchangedInvalid", "Keep-unchanged requires null content and an exact same-column prior cell.");
                }
                return Parsed(
                    new RecapCellExecutionOutcome.KeepUnchanged(
                        prepared.Work.EvaluationKey.Digest
                    ),
                    ignoredTextBlockCount,
                    ignoredTextUtf8Bytes
                );
            }
            if (!string.Equals(
                    outcome,
                    RecapRewriterProtocolV2.UpdatedOutcome,
                    StringComparison.Ordinal)
                || contentElement.ValueKind != JsonValueKind.String) {
                return Failed("TerminalOutcomeInvalid", "Updated requires string content; no other outcome is supported.");
            }
            if (ContainsUnpairedEscapedSurrogate(
                    contentElement.GetRawText())) {
                return Failed(
                    "UpdatedContentInvalidUtf16",
                    "Updated content contains an unpaired escaped surrogate."
                );
            }
            string content;
            try {
                content = contentElement.GetString()!;
            }
            catch (Exception exception) when (exception is JsonException
                or InvalidOperationException) {
                return Failed(
                    "UpdatedContentInvalidUtf16",
                    "Updated content contains invalid UTF-16."
                );
            }
            if (string.IsNullOrWhiteSpace(content)) {
                return Failed("UpdatedContentBlank", "Updated content must be non-blank.");
            }
            if (content.Contains(
                    RecapRewriterProtocolV2.ReservedProtocolToken,
                    StringComparison.Ordinal)) {
                return Failed(
                    "ReservedProtocolTokenInContent",
                    "Updated content contains the reserved V2 protocol token."
                );
            }
            int contentBytes;
            try {
                contentBytes = StrictUtf8.GetByteCount(content);
            }
            catch (EncoderFallbackException) {
                return Failed("UpdatedContentInvalidUtf16", "Updated content contains invalid UTF-16.");
            }
            int cap = Math.Min(
                prepared.Work.Definition.MaxContentUtf8Bytes,
                MaximumNeutralContentUtf8Bytes
            );
            if (contentBytes > cap) {
                return Failed("UpdatedContentTooLarge", "Updated content exceeds its exact V2 byte cap.");
            }
            return Parsed(
                new RecapCellExecutionOutcome.Updated(
                    prepared.Work.EvaluationKey.Digest,
                    content
                ),
                ignoredTextBlockCount,
                ignoredTextUtf8Bytes
            );
        }
        catch (JsonException exception) {
            return Failed("TerminalArgumentsInvalidJson", exception.Message);
        }
        catch (InvalidOperationException exception) {
            return Failed("TerminalArgumentsInvalidJson", exception.Message);
        }
    }

    private static RuntimeParseResult.Failed Failed(
        string code,
        string detail
    ) => new(code, detail);

    private static RuntimeParseResult.Failed InvalidTerminalEnvelope() =>
        Failed(
            "TerminalToolCallInvalid",
            "V2 requires optional reasoning and pre-terminal text, exactly one terminal tool call, and no post-terminal text or other output."
        );

    private static RuntimeParseResult.Parsed Parsed(
        RecapCellExecutionOutcome outcome,
        int ignoredTextBlockCount,
        int ignoredTextUtf8Bytes
    ) => new(
        outcome,
        ignoredTextBlockCount,
        ignoredTextUtf8Bytes
    );

    private static bool ContainsUnpairedEscapedSurrogate(string rawJson) {
        for (int index = 1; index < rawJson.Length - 1; index++) {
            if (rawJson[index] != '\\') { continue; }
            if (++index >= rawJson.Length - 1) { return true; }
            if (rawJson[index] != 'u') { continue; }
            if (!TryReadHexCodeUnit(rawJson, index + 1, out int value)) {
                return true;
            }
            index += 4;
            if (value is >= 0xDC00 and <= 0xDFFF) { return true; }
            if (value is not (>= 0xD800 and <= 0xDBFF)) { continue; }
            if (index + 6 >= rawJson.Length
                || rawJson[index + 1] != '\\'
                || rawJson[index + 2] != 'u'
                || !TryReadHexCodeUnit(
                    rawJson,
                    index + 3,
                    out int low
                )
                || low is not (>= 0xDC00 and <= 0xDFFF)) {
                return true;
            }
            index += 6;
        }
        return false;
    }

    private static bool TryReadHexCodeUnit(
        string value,
        int start,
        out int codeUnit
    ) {
        codeUnit = 0;
        if (start + 4 > value.Length) { return false; }
        for (int index = start; index < start + 4; index++) {
            int digit = value[index] switch {
                >= '0' and <= '9' => value[index] - '0',
                >= 'a' and <= 'f' => value[index] - 'a' + 10,
                >= 'A' and <= 'F' => value[index] - 'A' + 10,
                _ => -1
            };
            if (digit < 0) { return false; }
            codeUnit = (codeUnit << 4) | digit;
        }
        return true;
    }
}
