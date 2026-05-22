using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal static class NotebookBlockViewRenderer {
    internal static string RenderBlockView(TextBlockSnapshotDocument snapshot) {
        if (snapshot.Blocks.Count == 0) { return "(empty)"; }

        return string.Join(
            "\n",
            snapshot.Blocks.Select(static block => $"[{block.BlockId}] {block.Content}")
        );
    }

    internal static string RenderPreviewBlockView(
        TextBlockSnapshotDocument beforeSnapshot,
        TextBlockSnapshotDocument afterSnapshot
    ) {
        if (afterSnapshot.Blocks.Count == 0) { return "(empty)"; }

        var existingBlockIds = beforeSnapshot.Blocks
            .Select(static block => block.BlockId)
            .ToHashSet();
        var nextPreviewBlockOrdinal = 1;

        return string.Join(
            "\n",
            afterSnapshot.Blocks.Select(
                block => {
                    if (existingBlockIds.Contains(block.BlockId)) { return $"[{block.BlockId}] {block.Content}"; }

                    var label = $"new-{nextPreviewBlockOrdinal}";
                    nextPreviewBlockOrdinal++;
                    return $"[{label}] {block.Content}";
                }
            )
        );
    }
}
