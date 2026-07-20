using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Atelia.Completion.Abstractions;
using Atelia.Completion.Tools;
using Atelia.TextEditScript;

namespace Atelia.ChatSession.Memory;

public static class MemoryDocumentTools {
    public const string ReadToolName = "memory_document_read";
    public const string InsertToolName = "memory_document_insert";
    public const string ReplaceToolName = "memory_document_replace";
    public const string DeleteToolName = "memory_document_delete";
    public const string FinishToolName = "memory_document_finish_recording";

    public static ToolSession CreateSession(MemoryDocumentEditingSession editingSession) {
        ArgumentNullException.ThrowIfNull(editingSession);

        var host = new MemoryDocumentToolHost(editingSession);
        ITool[] tools = [
            MethodToolWrapper.FromMethod(host, typeof(MemoryDocumentToolHost).GetMethod(nameof(MemoryDocumentToolHost.ReadAsync))!),
            MethodToolWrapper.FromMethod(host, typeof(MemoryDocumentToolHost).GetMethod(nameof(MemoryDocumentToolHost.InsertAsync))!),
            MethodToolWrapper.FromMethod(host, typeof(MemoryDocumentToolHost).GetMethod(nameof(MemoryDocumentToolHost.ReplaceAsync))!),
            MethodToolWrapper.FromMethod(host, typeof(MemoryDocumentToolHost).GetMethod(nameof(MemoryDocumentToolHost.DeleteAsync))!),
            ArtifactToolWrapper<FinishRecordingArtifact>.Create(
                FinishToolName,
                (artifact, _) => host.Finish(artifact)
            )
        ];
        return new ToolRegistry(tools).CreateSession();
    }

    private sealed class MemoryDocumentToolHost(MemoryDocumentEditingSession editingSession) {
        [Tool(ReadToolName, "Read memory document blocks from a stable anchor. Returns block IDs and content; IDs are metadata, not document text.")]
        public ValueTask<ToolExecuteResult> ReadAsync(
            ReadMemoryBlocksInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return FromEditResult(editingSession.ReadBlocks(input.Anchor, input.MaxCount));
        }

        [Tool(InsertToolName, "Insert one memory block before or after a stable block-ID/head/tail anchor. Returns the new block ID.")]
        public ValueTask<ToolExecuteResult> InsertAsync(
            InsertMemoryBlockInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseSide(input.Side, out var side)) { return Failed("side must be exactly 'before' or 'after'."); }

            return FromEditResult(editingSession.Insert(side, input.Anchor, input.Content));
        }

        [Tool(ReplaceToolName, "Replace one memory block by stable block ID while preserving that ID.")]
        public ValueTask<ToolExecuteResult> ReplaceAsync(
            ReplaceMemoryBlockInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return FromEditResult(editingSession.Replace(input.Anchor, input.Content));
        }

        [Tool(DeleteToolName, "Delete one memory block by stable block ID/head/tail anchor. Other live block IDs remain unchanged.")]
        public ValueTask<ToolExecuteResult> DeleteAsync(
            DeleteMemoryBlockInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken
        ) {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return FromEditResult(editingSession.Delete(input.Anchor));
        }

        public ValidateResult Finish(FinishRecordingArtifact artifact) {
            if (!TryParseCompletionStatus(artifact.Status, out var status)) { return new ValidateResult(false, "status must be exactly 'changed' or 'no-change'."); }

            var result = editingSession.Finish(status);
            return new ValidateResult(result.IsSuccess, result.Message);
        }

        private static ValueTask<ToolExecuteResult> FromEditResult(MemoryDocumentEditResult result)
            => ValueTask.FromResult(
                ToolExecuteResult.FromText(
                    result.IsSuccess ? ToolExecutionStatus.Success : ToolExecutionStatus.Failed,
                    result.Message
                )
            );

        private static ValueTask<ToolExecuteResult> Failed(string message)
            => ValueTask.FromResult(ToolExecuteResult.FromText(ToolExecutionStatus.Failed, message));

        private static bool TryParseSide(string text, out TextInsertSide side) {
            switch (text) {
                case "before":
                    side = TextInsertSide.BeforeAnchor;
                    return true;
                case "after":
                    side = TextInsertSide.AfterAnchor;
                    return true;
                default:
                    side = default;
                    return false;
            }
        }

        private static bool TryParseCompletionStatus(
            string text,
            out MemoryDocumentRecordingCompletionStatus status
        ) {
            switch (text) {
                case "changed":
                    status = MemoryDocumentRecordingCompletionStatus.Changed;
                    return true;
                case "no-change":
                    status = MemoryDocumentRecordingCompletionStatus.NoChange;
                    return true;
                default:
                    status = default;
                    return false;
            }
        }
    }
}

[Description("Read a window of the current memory document.")]
public sealed record class ReadMemoryBlocksInput(
    [property: Description("Starting anchor: 'head', 'tail', or a visible decimal block ID.")]
    [property: JsonPropertyName("anchor")]
    string Anchor,
    [property: Description("Maximum number of consecutive blocks to return.")]
    [property: JsonPropertyName("maxCount")]
    [property: Range(1, 200)]
    int MaxCount
);

[Description("Insert one block into the current memory document.")]
public sealed record class InsertMemoryBlockInput(
    [property: Description("Insertion side: 'before' or 'after'.")]
    [property: JsonPropertyName("side")]
    [property: RegularExpression("^(before|after)$")]
    string Side,
    [property: Description("Anchor: 'head', 'tail', or a visible decimal block ID.")]
    [property: JsonPropertyName("anchor")]
    string Anchor,
    [property: Description("Block body only. Do not include block-ID display markers.")]
    [property: JsonPropertyName("content")]
    [property: MinLength(1)]
    string Content
);

[Description("Replace one block in the current memory document.")]
public sealed record class ReplaceMemoryBlockInput(
    [property: Description("Target anchor: a visible decimal block ID, 'head', or 'tail'.")]
    [property: JsonPropertyName("anchor")]
    string Anchor,
    [property: Description("Complete replacement body for this block. Do not include block-ID display markers.")]
    [property: JsonPropertyName("content")]
    [property: MinLength(1)]
    string Content
);

[Description("Delete one block from the current memory document.")]
public sealed record class DeleteMemoryBlockInput(
    [property: Description("Target anchor: a visible decimal block ID, 'head', or 'tail'.")]
    [property: JsonPropertyName("anchor")]
    string Anchor
);

[Description("Finish autobiographical recording after all edits. This tool must be called exactly once and by itself.")]
public sealed class FinishRecordingArtifact {
    [Description("Use 'changed' after one or more successful edits, or 'no-change' when no edit was needed.")]
    [JsonPropertyName("status")]
    [Required]
    [RegularExpression("^(changed|no-change)$")]
    public string Status { get; init; } = string.Empty;
}
