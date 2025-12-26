// Tests: Atelia.StateJournal.WorkspaceRecovery
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Recovery

using FluentAssertions;
using Xunit;
using WorkspaceClass = Atelia.StateJournal.Workspace;

namespace Atelia.StateJournal.Tests.Commit;

/// <summary>
/// WorkspaceRecovery 的单元测试。
/// </summary>
public class WorkspaceRecoveryTests {
    // ========================================================================
    // 空仓库测试
    // ========================================================================

    [Fact]
    public void Recover_EmptyMetaFile_ReturnsEmpty() {
        var info = WorkspaceRecovery.Recover(Array.Empty<MetaCommitRecord>(), 0);

        info.IsEmpty.Should().BeTrue();
        info.EpochSeq.Should().Be(0);
        info.NextObjectId.Should().Be(16);
        info.VersionIndexPtr.Should().Be(0);
        info.DataTail.Should().Be(0);
        info.WasTruncated.Should().BeFalse();
    }

    [Fact]
    public void Recover_EmptyMetaFileWithNonZeroDataSize_ReturnsEmpty() {
        // 即使 data file 有数据，没有 meta record 也返回空
        var info = WorkspaceRecovery.Recover(Array.Empty<MetaCommitRecord>(), 100);

        info.IsEmpty.Should().BeTrue();
        info.NextObjectId.Should().Be(16);
    }

    // ========================================================================
    // 正常恢复测试
    // ========================================================================

    [Fact]
    public void Recover_ValidRecord_ReturnsLatest() {
        var records = new[]
        {
            new MetaCommitRecord { EpochSeq = 1, NextObjectId = 17, DataTail = 100, VersionIndexPtr = 50 },
            new MetaCommitRecord { EpochSeq = 2, NextObjectId = 18, DataTail = 200, VersionIndexPtr = 150 },
        };

        var info = WorkspaceRecovery.Recover(records, 200);

        info.EpochSeq.Should().Be(2);
        info.NextObjectId.Should().Be(18);
        info.DataTail.Should().Be(200);
        info.VersionIndexPtr.Should().Be(150);
        info.WasTruncated.Should().BeFalse();
        info.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Recover_SingleValidRecord_ReturnsIt() {
        var records = new[]
        {
            new MetaCommitRecord { EpochSeq = 1, NextObjectId = 20, DataTail = 50, VersionIndexPtr = 25 },
        };

        var info = WorkspaceRecovery.Recover(records, 50);

        info.EpochSeq.Should().Be(1);
        info.NextObjectId.Should().Be(20);
        info.DataTail.Should().Be(50);
        info.VersionIndexPtr.Should().Be(25);
        info.WasTruncated.Should().BeFalse();
    }

    // ========================================================================
    // 截断场景测试
    // ========================================================================

    [Fact]
    public void Recover_DataLongerThanTail_IndicatesTruncation() {
        var records = new[]
        {
            new MetaCommitRecord { EpochSeq = 1, NextObjectId = 17, DataTail = 100, VersionIndexPtr = 50 },
        };

        // data file 比 DataTail 长（部分写入后崩溃）
        var info = WorkspaceRecovery.Recover(records, 150);

        info.EpochSeq.Should().Be(1);
        info.WasTruncated.Should().BeTrue();
        info.OriginalDataSize.Should().Be(150);
        info.DataTail.Should().Be(100);  // 应该截断到这里
    }

    [Fact]
    public void Recover_DataSlightlyLonger_IndicatesTruncation() {
        var records = new[]
        {
            new MetaCommitRecord { EpochSeq = 5, NextObjectId = 100, DataTail = 1000, VersionIndexPtr = 500 },
        };

        // 仅多出 1 字节
        var info = WorkspaceRecovery.Recover(records, 1001);

        info.WasTruncated.Should().BeTrue();
        info.OriginalDataSize.Should().Be(1001);
        info.DataTail.Should().Be(1000);
    }

    [Fact]
    public void Recover_DataExactlyMatchesTail_NoTruncation() {
        var records = new[]
        {
            new MetaCommitRecord { EpochSeq = 3, NextObjectId = 50, DataTail = 500, VersionIndexPtr = 250 },
        };

        var info = WorkspaceRecovery.Recover(records, 500);

        info.WasTruncated.Should().BeFalse();
        info.OriginalDataSize.Should().Be(0);  // 不需要记录原始大小
    }

    // ========================================================================
    // Meta 领先 Data 测试（撕裂提交）
    // ========================================================================

    [Fact]
    public void Recover_MetaAheadOfData_BacktracksToValidRecord() {
        var records = new[]
        {
            new MetaCommitRecord { EpochSeq = 1, NextObjectId = 17, DataTail = 100, VersionIndexPtr = 50 },
            new MetaCommitRecord { EpochSeq = 2, NextObjectId = 18, DataTail = 200, VersionIndexPtr = 150 },
            new MetaCommitRecord { EpochSeq = 3, NextObjectId = 19, DataTail = 300, VersionIndexPtr = 250 },  // 这条领先
        };

        // data file 只有 200 字节（第三次 commit 的 data 未完整写入）
        var info = WorkspaceRecovery.Recover(records, 200);

        // 应该回退到 epoch 2
        info.EpochSeq.Should().Be(2);
        info.NextObjectId.Should().Be(18);
        info.DataTail.Should().Be(200);
        info.VersionIndexPtr.Should().Be(150);
        info.WasTruncated.Should().BeFalse();  // 精确匹配，无需截断
    }

    [Fact]
    public void Recover_MetaAheadOfData_BacktracksMultipleLevels() {
        var records = new[]
        {
            new MetaCommitRecord { EpochSeq = 1, NextObjectId = 17, DataTail = 100, VersionIndexPtr = 50 },
            new MetaCommitRecord { EpochSeq = 2, NextObjectId = 18, DataTail = 200, VersionIndexPtr = 150 },
            new MetaCommitRecord { EpochSeq = 3, NextObjectId = 19, DataTail = 300, VersionIndexPtr = 250 },
            new MetaCommitRecord { EpochSeq = 4, NextObjectId = 20, DataTail = 400, VersionIndexPtr = 350 },
        };

        // data file 只有 150 字节（后三次 commit 的 data 都丢失）
        var info = WorkspaceRecovery.Recover(records, 150);

        // 应该回退到 epoch 1，且标记需要截断
        info.EpochSeq.Should().Be(1);
        info.NextObjectId.Should().Be(17);
        info.DataTail.Should().Be(100);
        info.WasTruncated.Should().BeTrue();
        info.OriginalDataSize.Should().Be(150);
    }

    [Fact]
    public void Recover_AllRecordsAheadOfData_ReturnsEmpty() {
        var records = new[]
        {
            new MetaCommitRecord { EpochSeq = 1, NextObjectId = 17, DataTail = 100, VersionIndexPtr = 50 },
        };

        // data file 为空（所有 data 都丢失）
        var info = WorkspaceRecovery.Recover(records, 0);

        info.IsEmpty.Should().BeTrue();
        info.EpochSeq.Should().Be(0);
        info.NextObjectId.Should().Be(16);
    }

    [Fact]
    public void Recover_MultipleRecordsAllAhead_ReturnsEmpty() {
        var records = new[]
        {
            new MetaCommitRecord { EpochSeq = 1, NextObjectId = 17, DataTail = 100, VersionIndexPtr = 50 },
            new MetaCommitRecord { EpochSeq = 2, NextObjectId = 18, DataTail = 200, VersionIndexPtr = 150 },
        };

        // data file 只有 50 字节，所有 record 都无效
        var info = WorkspaceRecovery.Recover(records, 50);

        info.IsEmpty.Should().BeTrue();
    }

    // ========================================================================
    // IsRecordValid 测试
    // ========================================================================

    [Fact]
    public void IsRecordValid_DataTailEqualsActual_ReturnsTrue() {
        var record = new MetaCommitRecord { DataTail = 100 };

        WorkspaceRecovery.IsRecordValid(record, 100).Should().BeTrue();
    }

    [Fact]
    public void IsRecordValid_DataTailLessThanActual_ReturnsTrue() {
        var record = new MetaCommitRecord { DataTail = 100 };

        WorkspaceRecovery.IsRecordValid(record, 150).Should().BeTrue();
    }

    [Fact]
    public void IsRecordValid_DataTailGreaterThanActual_ReturnsFalse() {
        var record = new MetaCommitRecord { DataTail = 100 };

        WorkspaceRecovery.IsRecordValid(record, 50).Should().BeFalse();
    }

    // ========================================================================
    // RecoveryInfo.Empty 测试
    // ========================================================================

    [Fact]
    public void RecoveryInfo_Empty_HasCorrectDefaults() {
        var empty = RecoveryInfo.Empty;

        empty.EpochSeq.Should().Be(0);
        empty.NextObjectId.Should().Be(16);
        empty.VersionIndexPtr.Should().Be(0);
        empty.DataTail.Should().Be(0);
        empty.WasTruncated.Should().BeFalse();
        empty.OriginalDataSize.Should().Be(0);
        empty.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void RecoveryInfo_IsEmpty_FalseForNonZeroEpoch() {
        var info = new RecoveryInfo { EpochSeq = 1 };

        info.IsEmpty.Should().BeFalse();
    }

    // ========================================================================
    // Workspace.Open 测试
    // ========================================================================

    [Fact]
    public void Workspace_Open_RestoresState() {
        var info = new RecoveryInfo {
            EpochSeq = 5,
            NextObjectId = 100,
            VersionIndexPtr = 0x5000,
            DataTail = 0x10000,
        };

        using var workspace = WorkspaceClass.Open(info);

        workspace.EpochSeq.Should().Be(5);
        workspace.NextObjectId.Should().Be(100);
        workspace.DataTail.Should().Be(0x10000);
        workspace.VersionIndexPtr.Should().Be(0x5000);
    }

    [Fact]
    public void Workspace_Open_EmptyRecoveryInfo_CreatesNewWorkspace() {
        var info = RecoveryInfo.Empty;

        using var workspace = WorkspaceClass.Open(info);

        workspace.EpochSeq.Should().Be(0);
        workspace.NextObjectId.Should().Be(16);
        workspace.DataTail.Should().Be(0);
        workspace.VersionIndexPtr.Should().Be(0);
    }

    [Fact]
    public void Workspace_Open_CanCreateObjects() {
        var info = new RecoveryInfo {
            EpochSeq = 3,
            NextObjectId = 50,
            VersionIndexPtr = 0x1000,
            DataTail = 0x2000,
        };

        using var workspace = WorkspaceClass.Open(info);

        // 恢复后可以继续创建对象
        var dict = workspace.CreateObject<DurableDict>();

        dict.ObjectId.Should().Be(50);  // 从恢复的 NextObjectId 开始
        workspace.NextObjectId.Should().Be(51);
    }

    [Fact]
    public void Workspace_Open_ThenCommit_IncrementsEpoch() {
        var info = new RecoveryInfo {
            EpochSeq = 10,
            NextObjectId = 200,
            VersionIndexPtr = 0,
            DataTail = 0,
        };

        using var workspace = WorkspaceClass.Open(info);

        // 创建一个对象使其有脏数据
        var dict = workspace.CreateObject<DurableDict>();
        dict.Set(1, 42L);

        // 提交
        var context = workspace.Commit();

        workspace.EpochSeq.Should().Be(11);  // 应该从恢复的 epoch 递增
    }
}
