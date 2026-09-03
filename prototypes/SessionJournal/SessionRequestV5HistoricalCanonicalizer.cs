using System.Buffers;
using System.Text.Json;
using Atelia.Completion.Abstractions;

namespace Atelia.SessionJournal;

/// <summary>
/// Recreates historical canonical-json-v1 bytes for commitment verification only. It never
/// creates or returns a <see cref="CompletionRequest"/>, and its legacy ceiling must not be used
/// for current execution.
/// </summary>
internal static class SessionRequestV5HistoricalCanonicalizer {
    public static byte[] Canonicalize(
        string modelId,
        CompletionPromptPrefix promptPrefix,
        IReadOnlyList<IHistoryMessage> tailMessages,
        int? legacyMaxTokens
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(promptPrefix);
        ArgumentNullException.ThrowIfNull(tailMessages);
        CompletionOutputContract outputContract = promptPrefix.OutputContract;
        if (!outputContract.IsProviderDefault) {
            throw new NotSupportedException(
                "Historical canonical-json-v1 cannot represent non-default tool-choice or parallel-call policy."
            );
        }
        if (tailMessages.Count != 0) {
            throw new NotSupportedException(
                "Historical canonical-json-v1 cannot represent a non-empty typed request tail."
            );
        }
        if (legacyMaxTokens is <= 0) {
            throw new ArgumentOutOfRangeException(
                nameof(legacyMaxTokens),
                "Historical maxTokens must be positive when present."
            );
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
            buffer,
            SessionRequestCanonicalizer.WriterOptions
        )) {
            writer.WriteStartObject();
            writer.WriteString("modelId", modelId);
            writer.WriteString("systemPrompt", promptPrefix.SystemPrompt);
            writer.WriteStartArray("context");
            foreach (IHistoryMessage message in promptPrefix.SharedContextMessages) {
                SessionRequestCanonicalizer.WriteHistoryMessage(writer, message);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("tools");
            foreach (ToolDefinition definition in outputContract.Tools) {
                SessionRequestCanonicalizer.WriteToolDefinition(writer, definition);
            }
            writer.WriteEndArray();
            if (legacyMaxTokens is int value) {
                writer.WriteNumber("maxTokens", value);
            }
            else {
                writer.WriteNull("maxTokens");
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenMemory.ToArray();
    }
}
