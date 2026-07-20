using System.Text;
using System.Text.RegularExpressions;
using Atelia.TextEditScript;

namespace Atelia.ChatSession.Memory;

public enum MemoryDocumentRecordingCompletionStatus {
    Changed,
    NoChange
}

public sealed record MemoryDocumentEditResult(
    bool IsSuccess,
    string Message,
    uint? NewBlockId = null
);

public sealed class MemoryDocumentEditingSession {
    private static readonly Regex s_blockSeparator = new(@"\n[\t ]*\n+", RegexOptions.Compiled);

    private TextBlockSnapshotDocument _workingDocument;
    private uint _nextInsertedBlockId;

    public MemoryDocumentEditingSession(string text) {
        BaseText = MemoryBlockTextNormalizer.NormalizeBlockText(text);
        _workingDocument = Blockize(BaseText);
        _nextInsertedBlockId = FindNextBlockId(_workingDocument.Blocks);
    }

    public string BaseText { get; }
    public TextBlockSnapshotDocument WorkingDocument => _workingDocument;
    public int EditCount { get; private set; }
    public MemoryDocumentRecordingCompletionStatus? CompletionStatus { get; private set; }
    public bool IsFinished => CompletionStatus is not null;

    public MemoryDocumentEditResult Insert(TextInsertSide side, string rawAnchor, string content) {
        if (TryRejectFinished(out var finished)) { return finished; }
        if (string.IsNullOrWhiteSpace(content)) { return Failed("Inserted block content cannot be empty."); }
        if (!TryParseAnchor(rawAnchor, out var anchor, out var error)) { return Failed(error); }
        if (_nextInsertedBlockId == 0) { return Failed("No block IDs remain available for insertion."); }

        var beforeIds = _workingDocument.Blocks.Select(static block => block.BlockId).ToHashSet();
        var result = Apply(new InsertTextEdit(side, anchor, content.Trim()));
        if (!result.IsSuccess) { return result; }

        var inserted = _workingDocument.Blocks.First(block => !beforeIds.Contains(block.BlockId));
        _nextInsertedBlockId = inserted.BlockId == uint.MaxValue ? 0 : inserted.BlockId + 1;
        return result with {
            Message = $"Inserted block {inserted.BlockId} {FormatSide(side)} anchor '{anchor}'.",
            NewBlockId = inserted.BlockId
        };
    }

    public MemoryDocumentEditResult Replace(string rawAnchor, string content) {
        if (TryRejectFinished(out var finished)) { return finished; }
        if (string.IsNullOrWhiteSpace(content)) { return Failed("Replacement block content cannot be empty. Use delete to remove a block."); }
        if (!TryParseAnchor(rawAnchor, out var anchor, out var error)) { return Failed(error); }
        return Apply(new ReplaceTextEdit(anchor, content.Trim()));
    }

    public MemoryDocumentEditResult Delete(string rawAnchor) {
        if (TryRejectFinished(out var finished)) { return finished; }
        if (!TryParseAnchor(rawAnchor, out var anchor, out var error)) { return Failed(error); }
        return Apply(new DeleteTextEdit(anchor));
    }

    public MemoryDocumentEditResult Finish(MemoryDocumentRecordingCompletionStatus status) {
        if (TryRejectFinished(out var finished)) { return finished; }

        if (status is MemoryDocumentRecordingCompletionStatus.Changed && EditCount == 0) { return Failed("Cannot finish with status 'changed' because no successful edits were made."); }

        if (status is MemoryDocumentRecordingCompletionStatus.NoChange && EditCount != 0) { return Failed("Cannot finish with status 'no-change' after successful edits."); }

        CompletionStatus = status;
        return Succeeded(
            status is MemoryDocumentRecordingCompletionStatus.Changed
            ? $"Recording completed with {EditCount} edits."
            : "Recording completed with no document changes."
        );
    }

    public string RenderDocumentText()
        => string.Join("\n\n", _workingDocument.Blocks.Select(static block => block.Content));

    public string RenderBlockView()
        => RenderBlocks(_workingDocument.Blocks);

    public MemoryDocumentEditResult ReadBlocks(string rawAnchor, int maxCount) {
        if (maxCount <= 0) { return Failed("maxCount must be greater than zero."); }
        if (!TryParseAnchor(rawAnchor, out var anchor, out var error)) { return Failed(error); }

        var blocks = _workingDocument.Blocks;
        if (blocks.Count == 0) { return Succeeded("(memory document is empty)"); }

        int startIndex = anchor.Kind switch {
            TextAnchorKind.Head => 0,
            TextAnchorKind.Tail => blocks.Count - 1,
            TextAnchorKind.BlockId => FindBlockIndex(blocks, anchor.BlockId),
            _ => -1
        };
        if (startIndex < 0) { return Failed($"Anchor block {anchor.BlockId} does not exist in the current document."); }

        return Succeeded(RenderBlocks(blocks.Skip(startIndex).Take(maxCount)));
    }

    private MemoryDocumentEditResult Apply(TextEditOperation operation) {
        var script = new TextEditScriptDocument([operation]);
        var applyResult = script.ApplyTo(
            _workingDocument,
            new TextEditScriptApplyOptions { FirstInsertedBlockId = _nextInsertedBlockId }
        );
        if (!applyResult.TryGetValue(out var updated) || updated is null) { return Failed(applyResult.Error?.Message ?? "The text edit failed without an error message."); }

        _workingDocument = updated;
        EditCount++;
        return Succeeded($"Applied {operation.GetType().Name}. Current block count: {_workingDocument.Blocks.Count}.");
    }

    private bool TryRejectFinished(out MemoryDocumentEditResult result) {
        if (!IsFinished) {
            result = null!;
            return false;
        }

        result = Failed("The recording session is already finished; no further edits are allowed.");
        return true;
    }

    private static TextBlockSnapshotDocument Blockize(string text) {
        if (string.IsNullOrWhiteSpace(text)) { return TextBlockSnapshotDocument.Empty; }

        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        string[] contents = s_blockSeparator.Split(normalized);
        var blocks = new TextBlockSnapshot[contents.Length];
        for (int i = 0; i < contents.Length; i++) {
            blocks[i] = new TextBlockSnapshot(checked((uint)i + 1), contents[i].Trim());
        }

        return new TextBlockSnapshotDocument(blocks);
    }

    private static uint FindNextBlockId(IReadOnlyList<TextBlockSnapshot> blocks) {
        uint maxBlockId = 0;
        for (int i = 0; i < blocks.Count; i++) {
            maxBlockId = Math.Max(maxBlockId, blocks[i].BlockId);
        }

        return maxBlockId == uint.MaxValue ? 0 : maxBlockId + 1;
    }

    private static int FindBlockIndex(IReadOnlyList<TextBlockSnapshot> blocks, uint blockId) {
        for (int i = 0; i < blocks.Count; i++) {
            if (blocks[i].BlockId == blockId) { return i; }
        }

        return -1;
    }

    private static string RenderBlocks(IEnumerable<TextBlockSnapshot> blocks) {
        var builder = new StringBuilder();
        foreach (var block in blocks) {
            if (builder.Length > 0) { builder.AppendLine(); }
            builder.Append("[block:").Append(block.BlockId).AppendLine("]");
            builder.AppendLine(block.Content);
            builder.Append("[/block]");
        }

        return builder.ToString();
    }

    private static bool TryParseAnchor(
        string rawAnchor,
        out TextAnchor anchor,
        out string error
    ) {
        var parseResult = TextAnchor.Parse(rawAnchor);
        if (parseResult.TryGetValue(out anchor)) {
            error = string.Empty;
            return true;
        }

        error = parseResult.Error?.Message ?? $"Invalid text anchor '{rawAnchor}'.";
        return false;
    }

    private static string FormatSide(TextInsertSide side)
        => side is TextInsertSide.BeforeAnchor ? "before" : "after";

    private static MemoryDocumentEditResult Succeeded(string message, uint? newBlockId = null)
        => new(true, message, newBlockId);

    private static MemoryDocumentEditResult Failed(string message)
        => new(false, message);
}
