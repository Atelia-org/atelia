using System.Globalization;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.StateJournal;

namespace Atelia.ChatSession;

public sealed record ChatSessionHistoryRecord(
    int Index,
    string Kind,
    DateTimeOffset? TimestampUtc,
    IHistoryMessage Message,
    RecapSourceAnchor? RecapSource
);

public enum ChatSessionMarkdownRecapMode {
    Include,
    Skip
}

public sealed record ChatSessionMarkdownExportOptions(
    ChatSessionMarkdownRecapMode RecapMode = ChatSessionMarkdownRecapMode.Include
);

public static class ChatSessionHistoryReader {
    public static IReadOnlyList<ChatSessionHistoryRecord> ReadCurrent(string repoDir, string branchName = "main") {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        using var repo = Repository.Open(repoDir).Unwrap();
        var revision = repo.CheckoutBranch(branchName).Unwrap();
        if (revision.GraphRoot is not DurableDict<string> root) { throw new InvalidDataException("Repository graph root is not a valid chat session."); }

        ChatSessionStorageSchema.ValidateRoot(root);
        var messages = ChatSessionStorageSchema.GetMessages(root);
        var result = new List<ChatSessionHistoryRecord>(messages.Count);

        for (int i = 0; i < messages.Count; i++) {
            if (!messages.TryGetAt<DurableDict<string>>(i, out var record) || record is null) { throw new InvalidDataException($"Message record at index {i} is not a durable message dict."); }

            var message = MessageRecord.ToHistoryMessage(record);
            var recapSource = message is RecapMessage recap ? recap.SourceAnchor : null;
            result.Add(
                new ChatSessionHistoryRecord(
                    Index: i,
                    Kind: MessageRecord.GetKind(record),
                    TimestampUtc: MessageRecord.GetTimestampUtc(record),
                    Message: message,
                    RecapSource: recapSource
                )
            );
        }

        return result;
    }
}

public static class ChatSessionMarkdownExporter {
    private const string Fence = "~~~~~~";

    public static string Export(
        IReadOnlyList<ChatSessionHistoryRecord> records,
        ChatSessionMarkdownExportOptions? options = null
    ) {
        ArgumentNullException.ThrowIfNull(records);
        options ??= new ChatSessionMarkdownExportOptions();

        var builder = new StringBuilder();
        for (int i = 0; i < records.Count; i++) {
            var record = records[i];
            if (record.Message is RecapMessage && options.RecapMode == ChatSessionMarkdownRecapMode.Skip) { continue; }

            if (builder.Length > 0) { builder.AppendLine(); }
            AppendRecord(builder, record);
        }

        return builder.ToString();
    }

    private static void AppendRecord(StringBuilder builder, ChatSessionHistoryRecord record) {
        builder.Append(CultureInfo.InvariantCulture, $"## {record.Index:D5} {record.Kind}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"- kind: {record.Kind}");
        builder.AppendLine();
        if (record.TimestampUtc is { } timestamp) {
            builder.Append(CultureInfo.InvariantCulture, $"- timestampUtc: {timestamp.ToString("O", CultureInfo.InvariantCulture)}");
            builder.AppendLine();
        }

        if (record.Message is RecapMessage) { AppendRecapSource(builder, record.RecapSource); }

        builder.AppendLine();
        switch (record.Message) {
            case ContextHeader contextHeader:
                AppendContextHeader(builder, contextHeader);
                break;
            case ToolResultsMessage toolResults:
                AppendToolResults(builder, toolResults);
                break;
            case ActionMessage action:
                AppendAction(builder, action);
                break;
            case ObservationMessage observation:
                AppendFence(builder, "text", observation.Content ?? string.Empty);
                break;
        }
    }

    private static void AppendRecapSource(StringBuilder builder, RecapSourceAnchor? source) {
        if (source is null) {
            builder.AppendLine("- recapSource: unresolved-recap");
            return;
        }

        builder.AppendLine("- recapSource: anchored");
        builder.Append(CultureInfo.InvariantCulture, $"- sourceHeadBeforeCompaction: {source.SourceHeadBeforeCompaction}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"- sourceBranchName: {source.SourceBranchName}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"- sourceStartIndex: {source.SourceStartIndex}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"- sourceEndExclusive: {source.SourceEndExclusive}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"- sourceMessageCountBefore: {source.SourceMessageCountBefore}");
        builder.AppendLine();
        builder.Append(CultureInfo.InvariantCulture, $"- compactionKind: {source.CompactionKind}");
        builder.AppendLine();
    }

    private static void AppendContextHeader(StringBuilder builder, ContextHeader header) {
        if (!string.IsNullOrEmpty(header.SystemPromptFragment)) {
            builder.AppendLine("### systemPromptFragment");
            AppendFence(builder, "text", header.SystemPromptFragment);
        }

        if (!string.IsNullOrEmpty(header.ObservationMessage)) {
            builder.AppendLine("### userMessage");
            AppendFence(builder, "text", header.ObservationMessage);
        }

        if (header.ActionMessage is not null) {
            builder.AppendLine("### assistantMessage");
            AppendAction(builder, header.ActionMessage);
        }
    }

    private static void AppendAction(StringBuilder builder, ActionMessage action) {
        AppendFence(builder, "text", action.GetFlattenedText());

        var toolCalls = action.ToolCalls;
        for (int i = 0; i < toolCalls.Count; i++) {
            var call = toolCalls[i];
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"### toolCall {i:D2}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- toolName: {FormatInline(call.ToolName)}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- toolCallId: {FormatInline(call.ToolCallId)}");
            builder.AppendLine();
            AppendFence(builder, "json", call.RawArgumentsJson);
        }
    }

    private static void AppendToolResults(StringBuilder builder, ToolResultsMessage message) {
        if (!string.IsNullOrEmpty(message.Content)) { AppendFence(builder, "text", message.Content); }

        for (int i = 0; i < message.Results.Count; i++) {
            var result = message.Results[i];
            if (i > 0 || !string.IsNullOrEmpty(message.Content)) { builder.AppendLine(); }
            builder.Append(CultureInfo.InvariantCulture, $"### toolResult {i:D2}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- toolName: {FormatInline(result.ToolName)}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- toolCallId: {FormatInline(result.ToolCallId)}");
            builder.AppendLine();
            builder.Append(CultureInfo.InvariantCulture, $"- status: {result.Status}");
            builder.AppendLine();
            AppendFence(builder, "text", result.GetFlattenedText());
        }
    }

    private static void AppendFence(StringBuilder builder, string language, string content) {
        builder.Append(Fence);
        builder.AppendLine(language);
        builder.AppendLine(content);
        builder.AppendLine(Fence);
    }

    private static string FormatInline(string value)
        => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
