namespace Atelia.TextEditScript;

public static class TextEditScriptApplier {
    public static AteliaResult<TextBlockSnapshotDocument> Apply(
        TextBlockSnapshotDocument snapshot,
        TextEditScriptDocument script,
        TextEditScriptApplyOptions? options = null) {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(script);

        var stateResult = MutableSnapshotState.Create(snapshot, options ?? new TextEditScriptApplyOptions());
        if (!stateResult.TryGetValue(out var state) || state is null) {
            return AteliaResult<TextBlockSnapshotDocument>.Failure(
                stateResult.Error ?? new TextEditScriptApplyError("Failed to initialize mutable snapshot state."));
        }

        foreach (var operation in script.Operations) {
            var applyResult = state.Apply(operation);
            if (!applyResult.IsSuccess) {
                return AteliaResult<TextBlockSnapshotDocument>.Failure(
                    applyResult.Error ?? new TextEditScriptApplyError("Apply returned failure without error details."));
            }
        }

        return new TextBlockSnapshotDocument(state.Export());
    }

    private sealed class MutableSnapshotState {
        private readonly List<TextBlockSnapshot> _blocks;
        private readonly Dictionary<uint, int> _indexByBlockId;
        private uint _nextInsertedBlockId;

        private MutableSnapshotState(
            List<TextBlockSnapshot> blocks,
            Dictionary<uint, int> indexByBlockId,
            uint nextInsertedBlockId) {
            _blocks = blocks;
            _indexByBlockId = indexByBlockId;
            _nextInsertedBlockId = nextInsertedBlockId;
        }

        public static AteliaResult<MutableSnapshotState> Create(
            TextBlockSnapshotDocument snapshot,
            TextEditScriptApplyOptions options) {
            var blocks = new List<TextBlockSnapshot>(snapshot.Blocks.Count);
            var indexByBlockId = new Dictionary<uint, int>();

            for (var i = 0; i < snapshot.Blocks.Count; i++) {
                var block = snapshot.Blocks[i];
                if (block.BlockId == 0) {
                    return AteliaResult<MutableSnapshotState>.Failure(
                        new TextEditScriptApplyError(
                            "Input snapshot contains block id 0, which is reserved and invalid.",
                            RecoveryHint: "Only live DurableText block ids greater than 0 may appear in a snapshot."));
                }

                if (!indexByBlockId.TryAdd(block.BlockId, i)) {
                    return AteliaResult<MutableSnapshotState>.Failure(
                        new TextEditScriptApplyError(
                            $"Input snapshot contains duplicate block id {block.BlockId}.",
                            RecoveryHint: "Provide each live block at most once, preserving current chain order."));
                }

                blocks.Add(block);
            }

            var nextInsertedBlockId = options.FirstInsertedBlockId ?? ComputeDefaultFirstInsertedBlockId(blocks);
            if (nextInsertedBlockId == 0) {
                return AteliaResult<MutableSnapshotState>.Failure(
                    new TextEditScriptApplyError(
                        "FirstInsertedBlockId must be greater than 0.",
                        RecoveryHint: "Leave it unset to auto-pick a preview id, or provide a positive id seed."));
            }

            return new MutableSnapshotState(blocks, indexByBlockId, nextInsertedBlockId);
        }

        public AteliaResult<Unit> Apply(TextEditOperation operation) {
            return operation switch {
                InsertTextEdit insert => ApplyInsert(insert),
                ReplaceTextEdit replace => ApplyReplace(replace),
                DeleteTextEdit delete => ApplyDelete(delete),
                _ => AteliaResult<Unit>.Failure(
                    new TextEditScriptApplyError($"Unsupported operation type: {operation.GetType().FullName}"))
            };
        }

        public IReadOnlyList<TextBlockSnapshot> Export()
            => _blocks.ToArray();

        private AteliaResult<Unit> ApplyInsert(InsertTextEdit operation) {
            var insertionIndexResult = ResolveInsertIndex(operation.Side, operation.Anchor);
            if (!insertionIndexResult.TryGetValue(out var insertionIndex)) {
                return AteliaResult<Unit>.Failure(insertionIndexResult.Error!);
            }

            var newBlockIdResult = AllocateInsertedBlockId();
            if (!newBlockIdResult.TryGetValue(out var newBlockId)) {
                return AteliaResult<Unit>.Failure(newBlockIdResult.Error!);
            }

            _blocks.Insert(insertionIndex, new TextBlockSnapshot(newBlockId, operation.Content));
            RebuildIndex();
            return Unit.Value;
        }

        private AteliaResult<Unit> ApplyReplace(ReplaceTextEdit operation) {
            var targetIndexResult = ResolveTargetIndex(operation.Anchor, operationName: "replace");
            if (!targetIndexResult.TryGetValue(out var targetIndex)) {
                return AteliaResult<Unit>.Failure(targetIndexResult.Error!);
            }

            var current = _blocks[targetIndex];
            _blocks[targetIndex] = current with { Content = operation.Content };
            return Unit.Value;
        }

        private AteliaResult<Unit> ApplyDelete(DeleteTextEdit operation) {
            var targetIndexResult = ResolveTargetIndex(operation.Anchor, operationName: "delete");
            if (!targetIndexResult.TryGetValue(out var targetIndex)) {
                return AteliaResult<Unit>.Failure(targetIndexResult.Error!);
            }

            _blocks.RemoveAt(targetIndex);
            RebuildIndex();
            return Unit.Value;
        }

        private AteliaResult<int> ResolveInsertIndex(TextInsertSide side, TextAnchor anchor) {
            if (_blocks.Count == 0) {
                return (anchor.Kind, side) switch {
                    (TextAnchorKind.Head, TextInsertSide.BeforeAnchor) => 0,
                    (TextAnchorKind.Tail, TextInsertSide.AfterAnchor) => 0,
                    _ => AteliaResult<int>.Failure(
                        new TextEditScriptApplyError(
                            $"Cannot insert {FormatSide(side)} anchor '{anchor}' into an empty snapshot.",
                            RecoveryHint: "When the snapshot is empty, use 'before head' or 'after tail' to create the first block."))
                };
            }

            return anchor.Kind switch {
                TextAnchorKind.BlockId => ResolveInsertIndexAroundBlockId(side, anchor.BlockId),
                TextAnchorKind.Head => side is TextInsertSide.BeforeAnchor ? 0 : 1,
                TextAnchorKind.Tail => side is TextInsertSide.BeforeAnchor ? _blocks.Count - 1 : _blocks.Count,
                _ => AteliaResult<int>.Failure(new TextEditScriptApplyError($"Unknown anchor kind: {anchor.Kind}"))
            };
        }

        private AteliaResult<int> ResolveInsertIndexAroundBlockId(TextInsertSide side, uint blockId) {
            if (!_indexByBlockId.TryGetValue(blockId, out var index)) {
                return AteliaResult<int>.Failure(
                    new TextEditScriptApplyError(
                        $"Insert anchor block id {blockId} does not exist in the current snapshot.",
                        RecoveryHint: "Use a live block id from the current notebook block view, or use 'head' / 'tail'."));
            }

            return side is TextInsertSide.BeforeAnchor ? index : index + 1;
        }

        private AteliaResult<int> ResolveTargetIndex(TextAnchor anchor, string operationName) {
            if (_blocks.Count == 0) {
                return AteliaResult<int>.Failure(
                    new TextEditScriptApplyError(
                        $"Cannot {operationName} anchor '{anchor}' because the snapshot is empty.",
                        RecoveryHint: "Insert a block first, or target an existing block id in a non-empty snapshot."));
            }

            return anchor.Kind switch {
                TextAnchorKind.Head => 0,
                TextAnchorKind.Tail => _blocks.Count - 1,
                TextAnchorKind.BlockId => ResolveBlockIdIndex(anchor.BlockId, operationName),
                _ => AteliaResult<int>.Failure(new TextEditScriptApplyError($"Unknown anchor kind: {anchor.Kind}"))
            };
        }

        private AteliaResult<int> ResolveBlockIdIndex(uint blockId, string operationName) {
            if (_indexByBlockId.TryGetValue(blockId, out var index)) {
                return index;
            }

            return AteliaResult<int>.Failure(
                new TextEditScriptApplyError(
                    $"Cannot {operationName} block id {blockId} because it does not exist in the current snapshot.",
                    RecoveryHint: "Use a live block id from the current notebook block view, or use 'head' / 'tail' when appropriate."));
        }

        private AteliaResult<uint> AllocateInsertedBlockId() {
            var candidate = _nextInsertedBlockId;
            while (candidate != 0 && _indexByBlockId.ContainsKey(candidate)) {
                candidate++;
            }

            if (candidate == 0) {
                return AteliaResult<uint>.Failure(
                    new TextEditScriptApplyError(
                        "Failed to allocate a preview block id because the uint range is exhausted.",
                        RecoveryHint: "Choose a larger FirstInsertedBlockId seed or reduce the preview block-id space."));
            }

            _nextInsertedBlockId = candidate + 1;
            return candidate;
        }

        private void RebuildIndex() {
            _indexByBlockId.Clear();
            for (var i = 0; i < _blocks.Count; i++) {
                _indexByBlockId[_blocks[i].BlockId] = i;
            }
        }

        private static uint ComputeDefaultFirstInsertedBlockId(IReadOnlyList<TextBlockSnapshot> blocks) {
            uint maxBlockId = 0;
            foreach (var block in blocks) {
                if (block.BlockId > maxBlockId) {
                    maxBlockId = block.BlockId;
                }
            }

            return maxBlockId + 1;
        }

        private static string FormatSide(TextInsertSide side) => side switch {
            TextInsertSide.BeforeAnchor => "before",
            TextInsertSide.AfterAnchor => "after",
            _ => side.ToString(),
        };
    }

    public readonly record struct Unit {
        public static Unit Value { get; } = new();
    }
}
