using System.Text;
using System.Text.RegularExpressions;
using Atelia.TextEditScript;

namespace Atelia.ChatSession.Memory;

public enum MemoryDocumentCompletionStatus {
    Changed,
    NoChange
}

public sealed record MemoryDocumentEditingOptions(
    bool ProtectFinalBlock = false
);

public sealed record MemoryDocumentEditResult(
    bool IsSuccess,
    string Message,
    uint? NewBlockId = null
);

public sealed class MemoryDocumentEditingSession {
    private static readonly Regex s_blockSeparator = new(@"\n[\t ]*\n+", RegexOptions.Compiled);

    private TextBlockSnapshotDocument _workingDocument;
    private uint _nextInsertedBlockId;

    private readonly uint? _protectedFinalBlockId;

    public MemoryDocumentEditingSession(
        string text,
        MemoryDocumentEditingOptions? options = null
    ) {
        BaseText = MemoryBlockTextNormalizer.NormalizeBlockText(text);
        _workingDocument = Blockize(BaseText);
        _nextInsertedBlockId = FindNextBlockId(_workingDocument.Blocks);
        if (options?.ProtectFinalBlock is true && _workingDocument.Blocks.Count > 0) {
            var finalBlock = _workingDocument.Blocks[^1];
            _protectedFinalBlockId = finalBlock.BlockId;
            ProtectedFinalBlockContent = finalBlock.Content;
        }
    }

    public string BaseText { get; }
    public TextBlockSnapshotDocument WorkingDocument => _workingDocument;
    public int EditCount { get; private set; }
    public MemoryDocumentCompletionStatus? CompletionStatus { get; private set; }
    public bool IsFinished => CompletionStatus is not null;
    public string? ProtectedFinalBlockContent { get; }

    public MemoryDocumentEditResult Insert(TextInsertSide side, string rawAnchor, string content) {
        if (TryRejectFinished(out var finished)) { return finished; }
        if (string.IsNullOrWhiteSpace(content)) { return Failed("Inserted block content cannot be empty."); }
        if (!TryParseAnchor(rawAnchor, out var anchor, out var error)) { return Failed(error); }
        if (_nextInsertedBlockId == 0) { return Failed("No block IDs remain available for insertion."); }
        if (IsInsertAfterProtectedFinalBlock(side, anchor)) { return Failed("Cannot insert after the protected final block because it must remain the document's final passage."); }

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
        if (TargetsProtectedFinalBlock(anchor)) { return Failed("Cannot replace the protected final block."); }
        return Apply(new ReplaceTextEdit(anchor, content.Trim()));
    }

    public MemoryDocumentEditResult ReplaceRange(
        string rawStartAnchor,
        string rawEndAnchor,
        string content
    ) {
        if (TryRejectFinished(out var finished)) { return finished; }
        if (string.IsNullOrWhiteSpace(content)) { return Failed("Replacement block content cannot be empty. Use delete to remove blocks."); }
        if (!TryParseAnchor(rawStartAnchor, out var startAnchor, out var startError)) { return Failed(startError); }
        if (!TryParseAnchor(rawEndAnchor, out var endAnchor, out var endError)) { return Failed(endError); }

        int startIndex = ResolveBlockIndex(startAnchor);
        int endIndex = ResolveBlockIndex(endAnchor);
        if (startIndex < 0 || endIndex < 0) { return Failed("Range anchors must refer to live blocks in the current document."); }
        if (startIndex > endIndex) { return Failed("Range start must not appear after range end."); }
        if (RangeIncludesProtectedFinalBlock(startIndex, endIndex)) { return Failed("Cannot replace a range that includes the protected final block."); }

        uint preservedBlockId = _workingDocument.Blocks[startIndex].BlockId;
        var operations = new List<TextEditOperation>(endIndex - startIndex + 1) {
            new ReplaceTextEdit(TextAnchor.ForBlockId(preservedBlockId), content.Trim())
        };
        for (int i = startIndex + 1; i <= endIndex; i++) {
            operations.Add(new DeleteTextEdit(TextAnchor.ForBlockId(_workingDocument.Blocks[i].BlockId)));
        }

        var result = Apply(operations);
        return result.IsSuccess
            ? result with { Message = $"Replaced range starting at block {preservedBlockId} through anchor '{endAnchor}' with one block." }
            : result;
    }

    public MemoryDocumentEditResult Delete(string rawAnchor) {
        if (TryRejectFinished(out var finished)) { return finished; }
        if (!TryParseAnchor(rawAnchor, out var anchor, out var error)) { return Failed(error); }
        if (TargetsProtectedFinalBlock(anchor)) { return Failed("Cannot delete the protected final block."); }
        return Apply(new DeleteTextEdit(anchor));
    }

    public MemoryDocumentEditResult Finish(MemoryDocumentCompletionStatus status) {
        if (TryRejectFinished(out var finished)) { return finished; }

        if (status is MemoryDocumentCompletionStatus.Changed && EditCount == 0) { return Failed("Cannot finish with status 'changed' because no successful edits were made."); }

        if (status is MemoryDocumentCompletionStatus.NoChange && EditCount != 0) { return Failed("Cannot finish with status 'no-change' after successful edits."); }

        CompletionStatus = status;
        return Succeeded(
            status is MemoryDocumentCompletionStatus.Changed
            ? $"Document maintenance completed with {EditCount} edits."
            : "Document maintenance completed with no changes."
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

    private MemoryDocumentEditResult Apply(TextEditOperation operation)
        => Apply([operation]);

    private MemoryDocumentEditResult Apply(IReadOnlyList<TextEditOperation> operations) {
        var script = new TextEditScriptDocument(operations);
        var applyResult = script.ApplyTo(
            _workingDocument,
            new TextEditScriptApplyOptions { FirstInsertedBlockId = _nextInsertedBlockId }
        );
        if (!applyResult.TryGetValue(out var updated) || updated is null) { return Failed(applyResult.Error?.Message ?? "The text edit failed without an error message."); }

        _workingDocument = updated;
        EditCount++;
        return Succeeded($"Applied {operations.Count} text edit operation(s). Current block count: {_workingDocument.Blocks.Count}.");
    }

    private bool TryRejectFinished(out MemoryDocumentEditResult result) {
        if (!IsFinished) {
            result = null!;
            return false;
        }

        result = Failed("The recording session is already finished; no further edits are allowed.");
        return true;
    }

    private bool IsInsertAfterProtectedFinalBlock(TextInsertSide side, TextAnchor anchor)
        => side is TextInsertSide.AfterAnchor && TargetsProtectedFinalBlock(anchor);

    private bool TargetsProtectedFinalBlock(TextAnchor anchor) {
        if (_protectedFinalBlockId is not uint protectedId) { return false; }

        return anchor.Kind switch {
            TextAnchorKind.Tail => true,
            TextAnchorKind.BlockId => anchor.BlockId == protectedId,
            _ => false
        };
    }

    private bool RangeIncludesProtectedFinalBlock(int startIndex, int endIndex) {
        if (_protectedFinalBlockId is not uint protectedId) { return false; }
        int protectedIndex = FindBlockIndex(_workingDocument.Blocks, protectedId);
        return protectedIndex >= startIndex && protectedIndex <= endIndex;
    }

    private int ResolveBlockIndex(TextAnchor anchor)
        => anchor.Kind switch {
            TextAnchorKind.Head => _workingDocument.Blocks.Count == 0 ? -1 : 0,
            TextAnchorKind.Tail => _workingDocument.Blocks.Count - 1,
            TextAnchorKind.BlockId => FindBlockIndex(_workingDocument.Blocks, anchor.BlockId),
            _ => -1
        };

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
