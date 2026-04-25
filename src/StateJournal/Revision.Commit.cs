using System.Buffers.Binary;
using Atelia.Diagnostics;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;

namespace Atelia.StateJournal;

partial class Revision {
    internal readonly record struct PrimaryCommitArtifacts(
        CommitTicket CommitTicket,
        List<DurableObject> LiveObjects
    );

    internal partial AteliaResult<CommitOutcome> Commit(DurableObject graphRoot, IRbfFile targetFile) {
        ArgumentNullException.ThrowIfNull(graphRoot);
        ArgumentNullException.ThrowIfNull(targetFile);

        string graphRootLabel = graphRoot.LocalId.Value.ToString();
        DebugUtil.Trace(
            "StateJournal.Commit",
            $"Start: graphRoot={graphRootLabel}, head={HeadId.Ticket.Serialize()}",
            eventKind: DebugEventKind.Start
        );

        var primaryCommit = RunPrimaryCommit(graphRoot, targetFile);
        if (primaryCommit.IsFailure) {
            DebugUtil.Warning(
                "StateJournal.Commit",
                $"Primary failed: graphRoot={graphRootLabel}, error={primaryCommit.Error!.ErrorCode}",
                eventKind: DebugEventKind.Failure
            );
            return primaryCommit.Error!;
        }

        var primaryArtifacts = primaryCommit.Value;
        CommitTicket primaryCommitTicket = primaryArtifacts.CommitTicket;
        DebugUtil.Info(
            "StateJournal.Commit",
            $"Primary succeeded: primary={primaryCommitTicket.Ticket.Serialize()}",
            eventKind: DebugEventKind.Success
        );
        return LogAndReturnOutcome(CommitOutcome.PrimaryOnly(primaryCommitTicket));
    }

    internal partial AteliaResult<CommitTicket> ExportTo(DurableObject graphRoot, IRbfFile targetFile) {
        ArgumentNullException.ThrowIfNull(graphRoot);
        ArgumentNullException.ThrowIfNull(targetFile);

        var liveObjectsResult = PrepareLiveObjects(graphRoot);
        if (liveObjectsResult.IsFailure) { return liveObjectsResult.Error!; }

        try {
            var persistResult = PersistCrossFileSnapshot(graphRoot, liveObjectsResult.Value!, targetFile);
            // ExportTo 不改变当前 Revision 状态，无论成败都回滚镜像层的未提交变更
            _objectMap.DiscardChanges();
            _symbolMirror.DiscardChanges();
            if (persistResult.IsFailure) { return persistResult.Error!; }
            // 不调用 FinalizePrimaryCommit：不 Complete PendingSave、不 Sweep、不更新 _head
            return persistResult.Value.Id;
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            _objectMap.DiscardChanges();
            _symbolMirror.DiscardChanges();
            return BuildStateError("ExportTo persistence failed", ex);
        }
    }

    internal partial AteliaResult<CommitOutcome> SaveAs(DurableObject graphRoot, IRbfFile targetFile) {
        ArgumentNullException.ThrowIfNull(graphRoot);
        ArgumentNullException.ThrowIfNull(targetFile);

        var liveObjectsResult = PrepareLiveObjects(graphRoot);
        if (liveObjectsResult.IsFailure) { return liveObjectsResult.Error!; }

        AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> persistResult;
        try {
            persistResult = PersistCrossFileSnapshot(graphRoot, liveObjectsResult.Value!, targetFile);
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            _objectMap.DiscardChanges();
            _symbolMirror.DiscardChanges();
            return BuildStateError("SaveAs persistence failed", ex);
        }
        if (persistResult.IsFailure) {
            _objectMap.DiscardChanges();
            _symbolMirror.DiscardChanges();
            return persistResult.Error!;
        }

        var (pendingSaves, newCommitTicket) = persistResult.Value;

        // Finalize: Complete all PendingSave (HeadTicket 指向新文件), Sweep GC, 更新 _head
        FinalizePrimaryCommit(graphRoot, liveObjectsResult.Value!, pendingSaves, newCommitTicket);
        // SaveAs 直接以全量快照作为新的 head，不再存在额外的后续整理阶段。
        return CommitOutcome.PrimaryOnly(newCommitTicket);
    }

    private static AteliaResult<CommitOutcome> LogAndReturnOutcome(CommitOutcome outcome) {
        var msg = $"Completed: head={outcome.HeadCommitTicket.Ticket.Serialize()}, kind={outcome.Completion}";
        DebugUtil.Info("StateJournal.Commit", msg, eventKind: DebugEventKind.Success);
        return outcome;
    }

    /// <summary>
    /// 执行 primary commit。
    /// </summary>
    /// <remarks>
    /// 此方法只应把“可归因于图状态 / 宿主环境 / I/O”的失败折叠为 <see cref="AteliaError"/>。
    /// 若内部持久化协议本身违反不变量，应让异常直接传播，以便 fail-fast 暴露实现 bug。
    /// </remarks>
    private AteliaResult<PrimaryCommitArtifacts> RunPrimaryCommit(DurableObject graphRoot, IRbfFile targetFile) {
        var liveObjectsResult = PrepareLiveObjects(graphRoot);
        if (liveObjectsResult.IsFailure) { return liveObjectsResult.Error!; }

        AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> persistResult;
        try {
            persistResult = PersistPrimarySnapshot(graphRoot, liveObjectsResult.Value!, targetFile);
        }
        catch (Exception ex) when (IsExternalCommitException(ex)) {
            _objectMap.DiscardChanges();
            _symbolMirror.DiscardChanges();
            return BuildStateError("Persistence failed", ex);
        }
        if (persistResult.IsFailure) {
            _objectMap.DiscardChanges();
            _symbolMirror.DiscardChanges();
            return persistResult.Error!;
        }

        var (pendingSaves, newCommitTicket) = persistResult.Value;

        // Phase 3: Finalize（全部落盘成功，统一应用内存状态变更）
        FinalizePrimaryCommit(graphRoot, liveObjectsResult.Value!, pendingSaves, newCommitTicket);
        return new PrimaryCommitArtifacts(newCommitTicket, liveObjectsResult.Value!);
    }

    private static SjStateError BuildStateError(string messagePrefix, Exception ex) {
        return new SjStateError(
            $"{messagePrefix}: {ex.GetType().Name}: {ex.Message}",
            RecoveryHint: "Revision state was not modified. Fix the issue and retry."
        );
    }

    private AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> PersistPrimarySnapshot(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        IRbfFile targetFile
    ) {
        return PersistSnapshotCore(
            graphRoot,
            liveObjects,
            frameSource: FrameSource.PrimaryCommit,
            targetFile,
            symbolMirrorUpdatePlan: SymbolMirrorUpdatePlan.ReachableScan(validateFullScan: true),
            removeUnreachableObjectMapKeys: true,
            forceAll: false
        );
    }

    private AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> PersistCrossFileSnapshot(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        IRbfFile targetFile
    ) {
        return PersistSnapshotCore(
            graphRoot,
            liveObjects,
            frameSource: FrameSource.CrossFileSnapshot,
            targetFile,
            symbolMirrorUpdatePlan: SymbolMirrorUpdatePlan.ReachableScan(validateFullScan: true),
            removeUnreachableObjectMapKeys: true,
            forceAll: true
        );
    }

    private AteliaResult<(List<PendingSave> PendingSaves, CommitTicket Id)> PersistSnapshotCore(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        FrameSource frameSource,
        IRbfFile targetFile,
        SymbolMirrorUpdatePlan symbolMirrorUpdatePlan,
        bool removeUnreachableObjectMapKeys,
        bool forceAll = false
    ) {
        var pendingSaves = new List<PendingSave>();
        var userContext = new DiffWriteContext(FrameUsage.UserPayload, frameSource) {
            // forceAll + targetFile 用于 ExportTo/SaveAs 的 full snapshot：
            // 我们强制当前可达对象全部写成 rebase 帧，但不抹掉对象现有的 HeadTicket。
            // 这样目标文件里的头帧会保留“逻辑祖先”信息；读取时因为头帧本身是 rebase，
            // Load 不会继续跟进这个 parent 去目标文件中追帧。
            ForceRebase = forceAll,
            ForceSave = forceAll,
        };
        foreach (var obj in liveObjects) {
            if (!forceAll && obj.IsTracked && !obj.HasChanges && !obj.HasMutabilityChanges) {
                if (obj.HasPendingObjectMapRegistration) {
                    _objectMap.Upsert(obj.LocalId.Value, obj.HeadTicket.Serialize());
                }
                continue;
            }
            var writeResult = VersionChain.Write(obj, targetFile, userContext);
            if (writeResult.IsFailure) { return writeResult.Error!; }
            pendingSaves.Add(writeResult.Value);
            _objectMap.Upsert(obj.LocalId.Value, writeResult.Value.Ticket.Serialize());
        }

        if (removeUnreachableObjectMapKeys) {
            // 从 ObjectMap 中移除不可达对象的 key（利用 Phase 1 的 mark bitmap 判定）
            // 迭代 CommittedKeys（上次 commit 的快照，本轮 Upsert/Remove 不影响）以避免临时集合分配
            foreach (uint key in _objectMap.CommittedKeys) {
                if (!_pool.IsMarkedReachable(new SlotHandle(key))) { _objectMap.Remove(key); }
            }
        }

        var symbolMirrorResult = UpdateSymbolMirror(symbolMirrorUpdatePlan);
        if (symbolMirrorResult.IsFailure) { return symbolMirrorResult.Error!; }

        // 写入 SymbolTable（它的 VersionChain 自动处理增量 diff）
        var stContext = new DiffWriteContext(FrameUsage.UserPayload, frameSource) {
            ForceRebase = forceAll,
            ForceSave = forceAll,
        };
        var stWriteResult = VersionChain.Write(_symbolMirror, targetFile, stContext);
        if (stWriteResult.IsFailure) { return stWriteResult.Error!; }
        var stPendingSave = stWriteResult.Value;
        pendingSaves.Add(stPendingSave);
        // SymbolTable 的 ticket 存入 ObjectMap，key = packed slot handle（slot 1）
        _objectMap.Upsert(new SlotHandle(0, 1).Packed, stPendingSave.Ticket.Serialize());

        // ── TailMeta: [0..3] GraphRoot LocalId, [4..7] SymbolTable LocalId ──
        Span<byte> rootMeta = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta, graphRoot.LocalId.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(rootMeta[4..], new SlotHandle(0, 1).Packed);

        DiffWriteContext mapContext = new(FrameUsage.ObjectMap, frameSource) {
            // ObjectMap 也保留逻辑祖先 commit 的 ticket。
            // 因此 Open(targetCommit, targetFile) 能读取当前快照，同时 HeadParentId 继续暴露这条跨文件祖先信息。
            ForceRebase = forceAll,
            ForceSave = true,
        };
        var mapWriteResult = VersionChain.Write(_objectMap, targetFile, mapContext, tailMeta: rootMeta);
        if (mapWriteResult.IsFailure) { return mapWriteResult.Error!; }
        pendingSaves.Add(mapWriteResult.Value);

        return (pendingSaves, new CommitTicket(mapWriteResult.Value.Ticket));
    }

    private void FinalizePrimaryCommit(
        DurableObject graphRoot,
        IReadOnlyList<DurableObject> liveObjects,
        List<PendingSave> pendingSaves,
        CommitTicket newCommitTicket
    ) {
        foreach (var pending in pendingSaves) { pending.Complete(); }
        foreach (var obj in liveObjects) { obj.ClearPendingObjectMapRegistration(); }

        // Mark bitmap 已在 Phase 1 (WalkAndMark) 中完成，直接 Sweep
        _pool.Sweep<DetachOnSweepCollectHandler>();
        _symbolPool.Sweep();

        _head = new CommitSnapshot(
            newCommitTicket,
            _head?.Id ?? default,
            _objectMap,
            graphRoot
        );
    }

    /// <summary>DFS 遍历中的 visitor：验证引用有效性 + 首访标记去重 + 收集存活 handle + 压入 DFS 栈。
    /// 同时标记 symbol pool 中的可达 SymbolId，一趟 DFS 完成两种 pool 的 mark。</summary>
    private ref partial struct WalkMarkVisitor(
            GcPool<DurableObject> pool,
            StringPool symbolPool,
            List<DurableObject> liveObjects,
            Stack<DurableObject> dfsStack) : IChildRefVisitor {
        public AteliaError? Error { get; private set; }
    }

    private ref partial struct ReferenceValidationVisitor(GcPool<DurableObject> pool, LocalId parentId) : IChildRefVisitor {
        public AteliaError? Error { get; private set; }
    }

    private static bool IsExternalCommitException(Exception ex) {
        return ex is IOException
            or UnauthorizedAccessException
            or ObjectDisposedException;
    }
}
