using System.Buffers;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.NodeContainers;
using Atelia.StateJournal.Serialization;
using Xunit;

namespace Atelia.StateJournal.Tests.NodeContainers;

public class TextSequenceCoreTests {
    private static Revision CreateRevision(uint segmentNumber = 1) => new(segmentNumber);

    [Fact]
    public void EstimatedRebaseBytes_CountsTextHeaderAndLiveNodeScaffold() {
        TextSequenceCore core = new();
        core.LoadBlocks(["alpha", "beta", "gamma"]);
        core.Commit();

        uint deletedId = core.GetAllBlocks()[1].Id;
        core.Delete(deletedId);

        // head + count + {0,0,liveCount} = 5
        // live nodes: seq + nextSeq + dummy key + BareSymbol ~= 1 + 1 + 1 + 5 = 8 each
        Assert.Equal(21u, core.EstimatedRebaseBytes());
    }

    [Fact]
    public void EstimatedDeltifyBytes_CountsDirtyLinkDirtyValueAndAppendedSections() {
        TextSequenceCore core = new();
        core.LoadBlocks(["a", "b"]);
        core.Commit();

        uint firstId = core.GetAllBlocks()[0].Id;
        uint secondId = core.GetAllBlocks()[1].Id;
        core.InsertAfter(firstId, "x");
        core.SetContent(secondId, "B");

        // header = head + count + dirtyLinkCount + dirtyValueCount + appendedCount = 5
        // dirty link entry = seq + nextSeq = 2
        // dirty value entry = seq + value = 1 + 5 = 6
        // appended entry = seq + nextSeq + dummy key + value = 1 + 1 + 1 + 5 = 8
        Assert.Equal(21u, core.EstimatedDeltifyBytes());
    }

    [Fact]
    public void EstimatedDeltifyBytes_AppendedWindowIncludesDeletedDraftNode() {
        TextSequenceCore core = new();
        core.LoadBlocks(["root"]);
        core.Commit();

        uint appendedId = core.Append("draft");
        core.Delete(appendedId);

        // 当前文本只剩 1 个 live block，但 delta appended section 仍写物理 appended window 中的 draft node。
        // header = 5, dirty link entry = 2, appended entry = 8
        Assert.Equal(15u, core.EstimatedDeltifyBytes());
    }

    [Fact]
    public void EstimatedTextBytes_RemainConsistentWithSerializationShape() {
        TextSequenceCore core = new();
        core.LoadBlocks(["a", "b"]);
        core.Commit();

        uint firstId = core.GetAllBlocks()[0].Id;
        uint secondId = core.GetAllBlocks()[1].Id;
        core.InsertAfter(firstId, "x");
        core.SetContent(secondId, "B");

        var revision = CreateRevision();

        var rebaseBuffer = new ArrayBufferWriter<byte>();
        var rebaseWriter = new BinaryDiffWriter(rebaseBuffer, revision);
        core.WriteRebase(rebaseWriter, DiffWriteContext.UserPrimary);

        uint deltaBytes = EstimateAssert.SerializedBodyBytes(
            writer => core.WriteDeltify(writer, DiffWriteContext.UserPrimary),
            revision
        );

        Assert.True(core.EstimatedRebaseBytes() >= rebaseBuffer.WrittenCount);
        Assert.True(core.EstimatedDeltifyBytes() >= deltaBytes);
    }
}
