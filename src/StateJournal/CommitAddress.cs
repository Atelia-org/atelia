using Atelia.Data;

namespace Atelia.StateJournal;

/// <summary>
/// commit 在物理存储中的坐标：{SegmentNumber, CommitTicket}。
/// 始终有效——SegmentNumber &gt; 0 且 CommitTicket 非 null。
/// "尚无 commit"的状态（unborn）由外部用 <c>CommitAddress?</c> 表达。
/// </summary>
internal readonly record struct CommitAddress(uint SegmentNumber, CommitTicket CommitTicket) {
    public static CommitAddress Create(uint segmentNumber, CommitTicket commitTicket) {
        if (segmentNumber == 0) { throw new InvalidOperationException("CommitAddress must have segmentNumber > 0."); }
        if (commitTicket.IsNull) { throw new InvalidOperationException("CommitAddress must have a non-null CommitTicket."); }

        return new CommitAddress(segmentNumber, commitTicket);
    }

    public static CommitAddress FromPersisted(uint segmentNumber, ulong ticketSerialized, string sourceDescription) {
        var commitTicket = new CommitTicket(SizedPtr.Deserialize(ticketSerialized));
        if (segmentNumber > 0 && !commitTicket.IsNull) { return Create(segmentNumber, commitTicket); }

        throw new InvalidDataException(
            $"Metadata '{sourceDescription}' contains an invalid commit address: segmentNumber={segmentNumber}, ticket={ticketSerialized}."
        );
    }
}
