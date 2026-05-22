using Atelia.StateJournal;
using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal static class GameNotebookEditService {
    internal static AteliaResult<NotebookEditProposal> Prepare(
        TextBlockSnapshotDocument beforeSnapshot,
        string scriptXml
    ) {
        var parseResult = TextEditScriptDocument.ParseXml(NormalizeNotebookEditInput(scriptXml));
        if (!parseResult.TryGetValue(out var script) || script is null) {
            return AteliaResult<NotebookEditProposal>.Failure(
                parseResult.Error ?? new TextAdvError(
                    "TextAdv.NotebookEdit.Parse",
                    "Failed to parse notebook edit script."
                )
            );
        }

        var anchorValidationResult = ValidateNumericAnchorsAgainstStartingSnapshot(beforeSnapshot, script);
        if (!anchorValidationResult.IsSuccess) { return AteliaResult<NotebookEditProposal>.Failure(anchorValidationResult.Error!); }

        var previewResult = DurableTextEditScriptExecutor.Preview(beforeSnapshot, script);
        if (!previewResult.TryGetValue(out var predictedAfterSnapshot) || predictedAfterSnapshot is null) {
            return AteliaResult<NotebookEditProposal>.Failure(
                previewResult.Error ?? new TextAdvError(
                    "TextAdv.NotebookEdit.ApplyPreview",
                    "Failed to preview notebook edit script."
                )
            );
        }

        var canonicalScriptXml = script.ToXml();
        return new NotebookEditProposal(
            canonicalScriptXml,
            script,
            predictedAfterSnapshot,
            BuildActionSummary(script),
            BuildValidatorPayload(beforeSnapshot, canonicalScriptXml, predictedAfterSnapshot)
        );
    }

    internal static void ApplyOrThrow(DurableText notebook, NotebookEditProposal proposal) {
        var applyResult = DurableTextEditScriptExecutor.Apply(notebook, proposal.Script);
        if (!applyResult.IsSuccess) {
            throw new InvalidOperationException(
                applyResult.Error?.Message
                ?? "Notebook edit execution failed without error details."
            );
        }
    }

    private static string BuildActionSummary(TextEditScriptDocument script) {
        if (script.Operations.Count == 1) {
            return script.Operations[0] switch {
                InsertTextEdit insert => $"insert notebook entry {DurableTextEditScriptExecutor.FormatInsertSide(insert.Side)} {insert.Anchor}",
                ReplaceTextEdit replace => $"replace notebook entry at {replace.Anchor}",
                DeleteTextEdit delete => $"delete notebook entry at {delete.Anchor}",
                _ => "apply notebook text-edit-script (1 op)",
            };
        }

        return $"apply notebook text-edit-script ({script.Operations.Count} ops)";
    }

    private static string BuildValidatorPayload(
        TextBlockSnapshotDocument beforeSnapshot,
        string canonicalScriptXml,
        TextBlockSnapshotDocument predictedAfterSnapshot
    ) {
        return string.Join(
            "\n\n",
            [
                "[TextEditScript XML]\n" + canonicalScriptXml,
                "[Predicted Memory-Notebook After Preview View]\n"
                + "(说明：新插入块暂以 [new-N] 标记；真正写入时才会分配实际 block id。)\n"
                + NotebookBlockViewRenderer.RenderPreviewBlockView(beforeSnapshot, predictedAfterSnapshot),
            ]
        );
    }

    private static string NormalizeNotebookEditInput(string rawInput) {
        var trimmed = rawInput.Trim();
        if (trimmed.Length == 0) { return trimmed; }

        if (trimmed.StartsWith("<text-edit-script", StringComparison.Ordinal)) { return trimmed; }

        return $"<text-edit-script>{trimmed}</text-edit-script>";
    }

    private static AteliaResult<Unit> ValidateNumericAnchorsAgainstStartingSnapshot(
        TextBlockSnapshotDocument beforeSnapshot,
        TextEditScriptDocument script
    ) {
        var liveBlockIds = beforeSnapshot.Blocks
            .Select(static block => block.BlockId)
            .ToHashSet();

        foreach (var operation in script.Operations) {
            TextAnchor? anchor = operation switch {
                InsertTextEdit insert => insert.Anchor,
                ReplaceTextEdit replace => replace.Anchor,
                DeleteTextEdit delete => delete.Anchor,
                _ => null,
            };

            if (anchor is not { Kind: TextAnchorKind.BlockId, BlockId: var blockId }) { continue; }

            if (liveBlockIds.Contains(blockId)) { continue; }

            return AteliaResult<Unit>.Failure(
                new TextAdvError(
                    "TextAdv.NotebookEdit.AnchorMustTargetLiveBlock",
                    $"Numeric anchor '{blockId}' does not exist in the current notebook block view.",
                    "Only use block ids that are already visible in the current notebook. If you need to edit a newly inserted block, submit it in a later small action, or use head/tail when appropriate."
                )
            );
        }

        return new Unit();
    }

    private readonly record struct Unit;
}
