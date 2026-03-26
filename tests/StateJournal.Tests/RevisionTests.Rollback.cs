using System.Reflection;
using Atelia.Data;
using Atelia.Rbf;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.Pools;
using Xunit;

namespace Atelia.StateJournal.Tests;

partial class RevisionTests {
    private sealed class FailOnNthBeginAppendFile(IRbfFile inner) : IRbfFile {
        private bool _armed;
        private int _beginAppendCount;
        private int _failOnBeginAppendIndex;

        public void Arm(int failOnBeginAppendIndex) {
            if (failOnBeginAppendIndex <= 0) { throw new ArgumentOutOfRangeException(nameof(failOnBeginAppendIndex)); }
            _armed = true;
            _beginAppendCount = 0;
            _failOnBeginAppendIndex = failOnBeginAppendIndex;
        }

        public long TailOffset => inner.TailOffset;

        public AteliaResult<SizedPtr> Append(uint tag, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> tailMeta = default) => inner.Append(tag, payload, tailMeta);

        public RbfFrameBuilder BeginAppend() {
            if (_armed && ++_beginAppendCount == _failOnBeginAppendIndex) {
                _armed = false;
                throw new IOException("Injected failure on compaction follow-up BeginAppend.");
            }
            return inner.BeginAppend();
        }
        public AteliaResult<RbfPooledFrame> ReadPooledFrame(SizedPtr ptr) => inner.ReadPooledFrame(ptr);
        public AteliaResult<RbfFrame> ReadFrame(SizedPtr ptr, Span<byte> buffer) => inner.ReadFrame(ptr, buffer);
        public RbfReverseSequence ScanReverse(bool showTombstone = false) => inner.ScanReverse(showTombstone);
        public AteliaResult<RbfFrameInfo> ReadFrameInfo(SizedPtr ticket) => inner.ReadFrameInfo(ticket);
        public AteliaResult<RbfTailMeta> ReadTailMeta(SizedPtr ticket, Span<byte> buffer) => inner.ReadTailMeta(ticket, buffer);
        public AteliaResult<RbfPooledTailMeta> ReadPooledTailMeta(SizedPtr ticket) => inner.ReadPooledTailMeta(ticket);
        public void DurableFlush() => inner.DurableFlush();
        public void Truncate(long newLengthBytes) => inner.Truncate(newLengthBytes);
        public void SetupReadLog(string? logPath) => inner.SetupReadLog(logPath);
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public void Commit_WhenCompactionTriggered_HeadParentPointsToIntermediateCommit() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        const int totalChildren = 140;
        for (int i = 0; i < totalChildren; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(i, i);
            root.Upsert(i, child);
        }

        var c1 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        var c2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(CommitCompletion.Compacted, c2.Completion);
        Assert.True(c2.IsCompacted);
        Assert.False(c2.IsCompactionRolledBack);

        Assert.Equal(c2.HeadCommitTicket, rev.HeadId);
        Assert.NotEqual(c1.HeadCommitTicket, rev.HeadParentId); // HeadParent 应指向内部中间 commit
        Assert.NotEqual(c2.PrimaryCommitTicket, c2.HeadCommitTicket);

        var opened = OpenRevision(c2.HeadCommitTicket, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");
        Assert.Equal(rev.HeadParentId, opened.Value!.HeadParentId);
        Assert.NotEqual(c1.HeadCommitTicket, opened.Value!.HeadParentId);
    }

    [Fact]
    public void Commit_WhenCompactionFollowupPersistFails_ReturnsRolledBackOutcomeWithPrimaryCommitDurable() {
        var path = GetTempFilePath();
        var inner = RbfFile.CreateNew(path);
        using var file = new FailOnNthBeginAppendFile(inner);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        const int totalChildren = 140;
        for (int i = 0; i < totalChildren; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(i, i);
            root.Upsert(i, child);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        Assert.Equal(GetIssue.None, root.Get(139, out var targetObj));
        var target = Assert.IsAssignableFrom<DurableDict<int, int>>(targetObj);
        LocalId targetIdBefore = target.LocalId;

        CommitTicket headBefore = rev.HeadId;

        int before = CountObjectMapFrames(file);
        file.Arm(failOnBeginAppendIndex: 4);
        var c2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(CommitCompletion.CompactionRolledBack, c2.Completion);
        Assert.True(c2.IsCompactionRolledBack);
        Assert.False(c2.IsCompacted);
        var err = Assert.IsType<SjCompactionPersistError>(c2.CompactionIssue);
        Assert.Equal("SJ.Compaction.FollowupPersistFailed", err.ErrorCode);
        Assert.True(err.Details?.ContainsKey("PrimaryCommitTicket"));
        Assert.Equal("FollowupPersist", err.Details!["CompactionStage"]);
        Assert.True(err.Details.ContainsKey("FollowupErrorCode"));
        int after = CountObjectMapFrames(file);
        Assert.Equal(before + 1, after); // 仅 primary commit 的 ObjectMap 已写入
        Assert.NotEqual(headBefore, rev.HeadId); // primary commit 已成功，Head 前进到中间提交
        Assert.Equal(c2.HeadCommitTicket, rev.HeadId);
        Assert.Equal(c2.PrimaryCommitTicket, c2.HeadCommitTicket);

        // follow-up persist 失败后应自动回滚 compaction，使内存状态重新对齐到 primary commit
        Assert.Equal(targetIdBefore, target.LocalId);
        Assert.Equal(GetIssue.None, root.Get(139, out var targetAfter));
        Assert.Same(target, targetAfter);
        Assert.Equal(targetIdBefore, targetAfter!.LocalId);

        Assert.Equal(rev.HeadId, rev.GraphRoot!.Revision.HeadId);
        var opened = OpenRevision(rev.HeadId, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.Value!.GraphRoot);
        Assert.Equal(70, loadedRoot.Count);

        Assert.Equal(GetIssue.None, loadedRoot.Get(139, out var loadedTargetObj));
        var loadedTarget = Assert.IsAssignableFrom<DurableDict<int, int>>(loadedTargetObj);
        Assert.Equal(target.LocalId, loadedTarget.LocalId);
    }

    [Fact]
    public void Commit_WhenSymbolOnlyCompactionFollowupPersistFails_RollsBackCleanly() {
        var path = GetTempFilePath();
        var inner = RbfFile.CreateNew(path);
        using var file = new FailOnNthBeginAppendFile(inner);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, string>();
        const int total = 200;
        const int toRemove = 100;

        for (int i = 0; i < total; i++) {
            root.Upsert(i, $"symbol_{i}");
        }
        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < toRemove; i++) {
            root.Remove(i);
        }

        int before = CountObjectMapFrames(file);
        file.Arm(failOnBeginAppendIndex: 4);
        var c2 = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit2");
        Assert.Equal(CommitCompletion.CompactionRolledBack, c2.Completion);
        Assert.True(c2.IsCompactionRolledBack);
        Assert.False(c2.IsCompacted);
        Assert.Equal(c2.PrimaryCommitTicket, c2.HeadCommitTicket);
        Assert.Equal(before + 1, CountObjectMapFrames(file));

        for (int i = toRemove; i < total; i++) {
            Assert.Equal(GetIssue.None, root.Get(i, out string? value));
            Assert.Equal($"symbol_{i}", value);
        }

        var opened = OpenRevision(rev.HeadId, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");
        var loaded = Assert.IsAssignableFrom<DurableDict<int, string>>(opened.Value!.GraphRoot);
        Assert.Equal(total - toRemove, loaded.Count);
        for (int i = toRemove; i < total; i++) {
            Assert.Equal(GetIssue.None, loaded.Get(i, out string? value));
            Assert.Equal($"symbol_{i}", value);
        }
    }

    [Fact]
    public void Commit_WhenCompactionApplyFails_FailsFastAndLeavesPrimaryCommitDurable() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        const int totalChildren = 140;
        for (int i = 0; i < totalChildren; i++) {
            var child = rev.CreateDict<int, int>();
            child.Upsert(i, i);
            root.Upsert(i, child);
        }

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        for (int i = 0; i < 70; i++) {
            root.Remove(i);
        }

        int framesBefore = CountObjectMapFrames(file);
        CommitTicket headBefore = rev.HeadId;

        using var faultScope = Revision.InjectCompactionFaultScope(
            Revision.CompactionFaultPoint.AfterFirstMoveApplied,
            static () => new InvalidOperationException("Injected failure during compaction apply.")
        );
        var ex = Assert.Throws<InvalidOperationException>(() => CommitToFile(rev, root, file));
        Assert.Contains("Injected failure during compaction apply", ex.Message);

        int framesAfter = CountObjectMapFrames(file);
        Assert.Equal(framesBefore + 1, framesAfter); // 只有 primary commit 的 ObjectMap 被写入
        Assert.NotEqual(headBefore, rev.HeadId); // 部分成功可见：Head 已推进到 primary commit

        // fail-fast 不承诺内存工作态仍可继续使用，但 primary commit 必须已经 durable。
        var opened = OpenRevision(rev.HeadId, file);
        Assert.True(opened.IsSuccess, $"Open failed: {opened.Error}");
        var loadedRoot = Assert.IsAssignableFrom<DurableDict<int, DurableDict<int, int>>>(opened.Value!.GraphRoot);
        Assert.Equal(70, loadedRoot.Count);
    }

    [Fact]
    public void TryRollbackCompaction_RestoresMovedObjectLocalId() {
        var path = GetTempFilePath();
        using var file = RbfFile.CreateNew(path);

        var rev = CreateRevision();
        _ = rev.CreateDict<int, int>(); // pad object: first commit 后会被 Sweep 掉，留下低位 hole
        var root = rev.CreateDict<int, DurableDict<int, int>>();
        var child = rev.CreateDict<int, int>();
        child.Upsert(7, 70);
        root.Upsert(1, child);

        _ = AssertCommitSucceeded(CommitToFile(rev, root, file), "Commit1");

        var poolField = typeof(Revision).GetField("_pool", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pool = Assert.IsAssignableFrom<GcPool<DurableObject>>(poolField.GetValue(rev));

        var applyResult = pool.CompactWithUndo(1);
        var record = Assert.Single(applyResult.Records);
        Assert.Equal(child.LocalId, new LocalId(record.OldHandle.Packed));

        var movedObj = pool[record.NewHandle];
        Assert.Same(child, movedObj);
        movedObj.Rebind(new LocalId(record.NewHandle.Packed)); // 模拟 compaction apply 已执行 Rebind
        Assert.Equal(new LocalId(record.NewHandle.Packed), child.LocalId);

        rev.RollbackCompactionChanges(applyResult);
        Assert.True(pool.Validate(record.OldHandle));
        Assert.Same(child, pool[record.OldHandle]);
        Assert.Equal(new LocalId(record.OldHandle.Packed), child.LocalId);
        Assert.False(pool.Validate(record.NewHandle));
    }
}
