using Atelia.Data;

namespace Atelia.StateJournal;

/// <summary>
/// commit 在物理存储中的坐标：{SegmentNumber, CommitId}。
/// 始终有效——SegmentNumber &gt; 0 且 CommitId 非 null。
/// "尚无 commit"的状态（unborn）由外部用 <c>CommitAddress?</c> 表达。
/// </summary>
internal readonly record struct CommitAddress(uint SegmentNumber, CommitId CommitId) {
    public static CommitAddress Create(uint segmentNumber, CommitId commitId) {
        if (segmentNumber == 0) { throw new InvalidOperationException("CommitAddress must have segmentNumber > 0."); }
        if (commitId.IsNull) { throw new InvalidOperationException("CommitAddress must have a non-null CommitId."); }

        return new CommitAddress(segmentNumber, commitId);
    }

    public static CommitAddress FromPersisted(uint segmentNumber, ulong ticketSerialized, string sourceDescription) {
        var commitId = new CommitId(SizedPtr.Deserialize(ticketSerialized));
        if (segmentNumber > 0 && !commitId.IsNull) { return Create(segmentNumber, commitId); }

        throw new InvalidDataException(
            $"Metadata '{sourceDescription}' contains an invalid commit address: segmentNumber={segmentNumber}, ticket={ticketSerialized}."
        );
    }
}
