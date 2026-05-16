using Atelia.StateJournal;
using Atelia.TextEditScript;

namespace Atelia.TextAdv;

internal static class DurableTextEditScriptExecutor {
    internal static AteliaResult<TextBlockSnapshotDocument> Preview(
        TextBlockSnapshotDocument beforeSnapshot,
        TextEditScriptDocument script
    ) {
        var previewTarget = new PreviewNotebookEditTarget(beforeSnapshot);
        var previewApplyResult = Execute(
            script,
            previewTarget,
            beforeSnapshot.Blocks.Select(static block => block.BlockId).ToArray()
        );
        if (!previewApplyResult.IsSuccess) { return AteliaResult<TextBlockSnapshotDocument>.Failure(previewApplyResult.Error!); }

        return previewTarget.ExportSnapshot();
    }

    internal static AteliaResult<Unit> Apply(DurableText notebook, TextEditScriptDocument script)
        => Execute(
            script,
            new DurableNotebookEditTarget(notebook),
            notebook.GetAllBlocks().Select(static block => block.Id).ToArray()
        );

    internal static string FormatInsertSide(TextInsertSide side) => side switch {
        TextInsertSide.BeforeAnchor => "before",
        TextInsertSide.AfterAnchor => "after",
        _ => side.ToString(),
    };

    private static AteliaResult<Unit> Execute(
        TextEditScriptDocument script,
        INotebookEditTarget target,
        IReadOnlyList<uint> startingBlockIds
    ) {
        var currentBlockIds = startingBlockIds.ToList();
        foreach (var operation in script.Operations) {
            AteliaResult<Unit> operationResult = operation switch {
                InsertTextEdit insert => ExecuteInsert(target, currentBlockIds, insert),
                ReplaceTextEdit replace => ExecuteReplace(target, currentBlockIds, replace),
                DeleteTextEdit delete => ExecuteDelete(target, currentBlockIds, delete),
                _ => AteliaResult<Unit>.Failure(
                    new TextAdvError(
                        "TextAdv.NotebookEdit.UnsupportedOperation",
                        $"Unsupported notebook edit operation: {operation.GetType().FullName}"
                    )
                ),
            };

            if (!operationResult.IsSuccess) { return AteliaResult<Unit>.Failure(operationResult.Error!); }
        }

        return new Unit();
    }

    private static AteliaResult<Unit> ExecuteInsert(
        INotebookEditTarget target,
        List<uint> currentBlockIds,
        InsertTextEdit insert
    ) {
        if (currentBlockIds.Count == 0) {
            if ((insert.Anchor.Kind, insert.Side) is (TextAnchorKind.Head, TextInsertSide.BeforeAnchor)
                or (TextAnchorKind.Tail, TextInsertSide.AfterAnchor)) {
                currentBlockIds.Add(target.Append(insert.Content));
                return new Unit();
            }

            return AteliaResult<Unit>.Failure(
                new TextAdvError(
                    "TextAdv.NotebookEdit.InvalidEmptyInsert",
                    $"Cannot insert {FormatInsertSide(insert.Side)} {insert.Anchor} into an empty notebook.",
                    "When the notebook is empty, use 'before head' or 'after tail' to create the first block."
                )
            );
        }

        switch (insert.Anchor.Kind) {
            case TextAnchorKind.BlockId:
                var targetIndexResult = ResolveCurrentAnchorIndex(currentBlockIds, insert.Anchor, operationName: "insert");
                if (!targetIndexResult.TryGetValue(out var targetIndex)) { return AteliaResult<Unit>.Failure(targetIndexResult.Error!); }

                var anchorBlockId = currentBlockIds[targetIndex];
                if (insert.Side is TextInsertSide.BeforeAnchor) {
                    currentBlockIds.Insert(targetIndex, target.InsertBefore(anchorBlockId, insert.Content));
                }
                else {
                    currentBlockIds.Insert(targetIndex + 1, target.InsertAfter(anchorBlockId, insert.Content));
                }

                return new Unit();

            case TextAnchorKind.Tail:
                var tailId = currentBlockIds[^1];
                if (insert.Side is TextInsertSide.BeforeAnchor) {
                    currentBlockIds.Insert(currentBlockIds.Count - 1, target.InsertBefore(tailId, insert.Content));
                }
                else {
                    currentBlockIds.Add(target.InsertAfter(tailId, insert.Content));
                }

                return new Unit();

            case TextAnchorKind.Head:
                var headId = currentBlockIds[0];
                if (insert.Side is TextInsertSide.BeforeAnchor) {
                    currentBlockIds.Insert(0, target.InsertBefore(headId, insert.Content));
                }
                else {
                    currentBlockIds.Insert(1, target.InsertAfter(headId, insert.Content));
                }

                return new Unit();

            default:
                return AteliaResult<Unit>.Failure(
                    new TextAdvError(
                        "TextAdv.NotebookEdit.UnknownInsertAnchorKind",
                        $"Unknown insert anchor kind: {insert.Anchor.Kind}"
                    )
                );
        }
    }

    private static AteliaResult<Unit> ExecuteReplace(
        INotebookEditTarget target,
        List<uint> currentBlockIds,
        ReplaceTextEdit replace
    ) {
        var targetIndexResult = ResolveCurrentAnchorIndex(currentBlockIds, replace.Anchor, operationName: "replace");
        if (!targetIndexResult.TryGetValue(out var targetIndex)) { return AteliaResult<Unit>.Failure(targetIndexResult.Error!); }

        target.SetContent(currentBlockIds[targetIndex], replace.Content);
        return new Unit();
    }

    private static AteliaResult<Unit> ExecuteDelete(
        INotebookEditTarget target,
        List<uint> currentBlockIds,
        DeleteTextEdit delete
    ) {
        var targetIndexResult = ResolveCurrentAnchorIndex(currentBlockIds, delete.Anchor, operationName: "delete");
        if (!targetIndexResult.TryGetValue(out var targetIndex)) { return AteliaResult<Unit>.Failure(targetIndexResult.Error!); }

        target.Delete(currentBlockIds[targetIndex]);
        currentBlockIds.RemoveAt(targetIndex);
        return new Unit();
    }

    private static AteliaResult<int> ResolveCurrentAnchorIndex(
        List<uint> currentBlockIds,
        TextAnchor anchor,
        string operationName
    ) {
        if (currentBlockIds.Count == 0) {
            return AteliaResult<int>.Failure(
                new TextAdvError(
                    "TextAdv.NotebookEdit.EmptyNotebookAnchor",
                    $"Cannot {operationName} anchor '{anchor}' because the notebook is empty.",
                    "Insert a first block with 'before head' or 'after tail' before targeting it."
                )
            );
        }

        return anchor.Kind switch {
            TextAnchorKind.Head => 0,
            TextAnchorKind.Tail => currentBlockIds.Count - 1,
            TextAnchorKind.BlockId => ResolveCurrentBlockIdIndex(currentBlockIds, anchor.BlockId, operationName),
            _ => AteliaResult<int>.Failure(
                new TextAdvError(
                    "TextAdv.NotebookEdit.UnknownAnchorKind",
                    $"Unknown anchor kind: {anchor.Kind}"
                )
            ),
        };
    }

    private static AteliaResult<int> ResolveCurrentBlockIdIndex(
        List<uint> currentBlockIds,
        uint blockId,
        string operationName
    ) {
        var index = currentBlockIds.IndexOf(blockId);
        if (index >= 0) { return index; }

        return AteliaResult<int>.Failure(
            new TextAdvError(
                "TextAdv.NotebookEdit.AnchorNoLongerLive",
                $"Cannot {operationName} block id {blockId} because it is not live in the current notebook state.",
                "Only target a block id that is still visible in the current notebook block view, or use head/tail when appropriate."
            )
        );
    }

    private interface INotebookEditTarget {
        uint InsertBefore(uint beforeBlockId, string content);

        uint InsertAfter(uint afterBlockId, string content);

        uint Append(string content);

        void SetContent(uint blockId, string content);

        void Delete(uint blockId);
    }

    private sealed class DurableNotebookEditTarget(DurableText notebook) : INotebookEditTarget {
        public uint InsertBefore(uint beforeBlockId, string content) => notebook.InsertBefore(beforeBlockId, content);

        public uint InsertAfter(uint afterBlockId, string content) => notebook.InsertAfter(afterBlockId, content);

        public uint Append(string content) => notebook.Append(content);

        public void SetContent(uint blockId, string content) => notebook.SetContent(blockId, content);

        public void Delete(uint blockId) => notebook.Delete(blockId);
    }

    private sealed class PreviewNotebookEditTarget : INotebookEditTarget {
        private readonly List<TextBlockSnapshot> _blocks;
        private uint _nextPreviewBlockId;

        public PreviewNotebookEditTarget(TextBlockSnapshotDocument snapshot) {
            _blocks = snapshot.Blocks.ToList();
            _nextPreviewBlockId = ComputeNextPreviewBlockId(snapshot.Blocks);
        }

        public uint InsertBefore(uint beforeBlockId, string content) {
            var index = FindBlockIndex(beforeBlockId);
            var newBlockId = AllocatePreviewBlockId();
            _blocks.Insert(index, new TextBlockSnapshot(newBlockId, content));
            return newBlockId;
        }

        public uint InsertAfter(uint afterBlockId, string content) {
            var index = FindBlockIndex(afterBlockId);
            var newBlockId = AllocatePreviewBlockId();
            _blocks.Insert(index + 1, new TextBlockSnapshot(newBlockId, content));
            return newBlockId;
        }

        public uint Append(string content) {
            var newBlockId = AllocatePreviewBlockId();
            _blocks.Add(new TextBlockSnapshot(newBlockId, content));
            return newBlockId;
        }

        public void SetContent(uint blockId, string content) {
            var index = FindBlockIndex(blockId);
            _blocks[index] = _blocks[index] with { Content = content };
        }

        public void Delete(uint blockId) {
            _blocks.RemoveAt(FindBlockIndex(blockId));
        }

        public TextBlockSnapshotDocument ExportSnapshot() => new(_blocks.ToArray());

        private int FindBlockIndex(uint blockId) {
            for (var index = 0; index < _blocks.Count; index++) {
                if (_blocks[index].BlockId == blockId) { return index; }
            }

            throw new InvalidOperationException($"Preview target cannot find block id {blockId}.");
        }

        private uint AllocatePreviewBlockId() {
            var candidate = _nextPreviewBlockId;
            while (_blocks.Any(block => block.BlockId == candidate)) {
                candidate++;
            }

            _nextPreviewBlockId = candidate + 1;
            return candidate;
        }
    }

    private static uint ComputeNextPreviewBlockId(IReadOnlyList<TextBlockSnapshot> blocks) {
        uint maxBlockId = 0;
        foreach (var block in blocks) {
            if (block.BlockId > maxBlockId) {
                maxBlockId = block.BlockId;
            }
        }

        return maxBlockId + 1;
    }

    internal readonly record struct Unit;
}
