// Source: Atelia.StateJournal.Tests - CommitContext 测试
// Spec: atelia/docs/StateJournal/mvp-design-v2.md §Two-Phase Commit

using Atelia.StateJournal;
using FluentAssertions;
using Xunit;
using WorkspaceClass = Atelia.StateJournal.Workspace;

namespace Atelia.StateJournal.Tests.Commit;

/// <summary>
/// CommitContext 单元测试。
/// </summary>
public class CommitContextTests {
    // ========================================================================
    // BuildMetaCommitRecord 测试
    // ========================================================================

    [Fact]
    public void BuildMetaCommitRecord_ContainsCorrectValues() {
        // Arrange
        var context = new CommitContext {
            EpochSeq = 5,
            DataTail = 1024,
            VersionIndexPtr = 512,
            RootObjectId = 100,
        };

        // Act
        var record = context.BuildMetaCommitRecord(nextObjectId: 50);

        // Assert
        record.EpochSeq.Should().Be(5);
        record.RootObjectId.Should().Be(100);
        record.VersionIndexPtr.Should().Be(512);
        record.DataTail.Should().Be(1024);
        record.NextObjectId.Should().Be(50);
    }

    [Fact]
    public void BuildMetaCommitRecord_WithZeroValues_CreatesValidRecord() {
        // Arrange
        var context = new CommitContext {
            EpochSeq = 1,
            DataTail = 0,
            VersionIndexPtr = 0,
            RootObjectId = 0,
        };

        // Act
        var record = context.BuildMetaCommitRecord(nextObjectId: 16);

        // Assert
        record.EpochSeq.Should().Be(1);
        record.RootObjectId.Should().Be(0);
        record.VersionIndexPtr.Should().Be(0);
        record.DataTail.Should().Be(0);
        record.NextObjectId.Should().Be(16);
    }

    [Fact]
    public void BuildMetaCommitRecord_IntegratedWithWorkspace_ContainsCorrectValues() {
        // Arrange
        using var workspace = new WorkspaceClass();
        var dict = workspace.CreateObject<DurableDict<long?>>();
        dict.Set(1, 100);

        var context = workspace.PrepareCommit();

        // Act
        var record = context.BuildMetaCommitRecord(workspace.NextObjectId);

        // Assert
        record.EpochSeq.Should().Be(1);
        record.NextObjectId.Should().Be(17);  // 创建了一个对象
        record.DataTail.Should().BeGreaterThan(0);
        record.VersionIndexPtr.Should().BeGreaterThan(0);
    }

    // ========================================================================
    // WriteObjectVersion 测试
    // ========================================================================

    [Fact]
    public void WriteObjectVersion_ReturnsPosition() {
        // Arrange
        var context = new CommitContext {
            EpochSeq = 1,
            DataTail = 100,
        };

        // Act
        var position = context.WriteObjectVersion(16, new byte[] { 1, 2, 3 }, 0x1001);

        // Assert
        position.Should().Be(100);  // 返回写入前的位置
    }

    [Fact]
    public void WriteObjectVersion_IncreasesDataTail() {
        // Arrange
        var context = new CommitContext {
            EpochSeq = 1,
            DataTail = 100,
        };

        // Act
        context.WriteObjectVersion(16, new byte[] { 1, 2, 3 }, 0x1001);

        // Assert
        // 8 (FrameHeader) + 3 (payload) + 4 (CRC) = 15
        context.DataTail.Should().Be(115);
    }

    [Fact]
    public void WriteObjectVersion_AddsToWrittenRecords() {
        // Arrange
        var context = new CommitContext {
            EpochSeq = 1,
            DataTail = 0,
        };
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        context.WriteObjectVersion(42, payload, 0x1001);

        // Assert
        context.WrittenRecords.Should().HaveCount(1);
        context.WrittenRecords[0].ObjectId.Should().Be(42);
        context.WrittenRecords[0].DiffPayload.Should().BeEquivalentTo(payload);
        context.WrittenRecords[0].FrameTag.Should().Be(0x1001);
    }
}
