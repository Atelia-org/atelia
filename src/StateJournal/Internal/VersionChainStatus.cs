using Atelia.Data;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

internal struct VersionChainStatus {
    // const uint ReadWeight = 1, WriteWeight = 1, StorageWeight = 1;
    const uint MaxReadAmplificationRatio = 3; // 来自于权重分配方案`Write:Read:Storage = 1:1:1`

    // 固定额外开销相当于多少条变更（remove or upsert）。每条变更估算约VarInt(8B+8B)≈8B
    const uint PerFrameOverhead = 6; // 涉及多个变长字段，还挺不好估算的，约34B。RbfFrame=24B, ParentTicket+CumulativeCost=VarInt(8B+4B), RemoveCount+UpsertCount=VarInt(4B+4B)。

    // 前一个version的RBF Frame Ticket。_head不用于标记是否为rebase version, 对于 rebase version 依然可能尽可能保持不断版本历史，
    SizedPtr _head;

    // 从头帧的 parentTicket 指向的基帧（含）到当前 _head（含）总共的开销。用于评估是否值得以deltify方式保存而非rebase。
    uint _cumulativeCost; // 由于PerFrameOverhead的存在，在初始化后_cumulativeCost不可能为0，可用于初始化检测。

    internal readonly SizedPtr Head => _head;
    internal readonly bool IsTracked => _cumulativeCost != 0;
    /// <summary>Load 完成后修正 _previousVersion，使其指向已加载版本本身而非其前驱帧。</summary>
    internal void SetHead(SizedPtr ticket) => _head = ticket;

    // uint _diagChainDepth; // 暂未找到明确的用途，先注释掉了
    // uint _diagPreviousSize; // 暂未找到明确的用途，先注释掉了

    internal bool ShouldRebase(uint rebaseSize, uint deltifySize) {
        // 首次保存（_cumulativeCost == 0）必须 rebase，否则帧没有 typeCode，Load 无法终止回溯。
        if (_cumulativeCost == 0) { return true; }

        return rebaseSize <= deltifySize
            || (rebaseSize - deltifySize) * MaxReadAmplificationRatio <= _cumulativeCost;
    }

    private readonly uint GetDeltifiedCumulativeCost(uint deltifySize) => checked(_cumulativeCost + deltifySize + PerFrameOverhead);
    /// <summary>二阶段提交的前一阶段</summary>
    internal readonly void WriteDeltify(IDiffWriter writer, uint deltifySize) {
        uint newCumulativeCost = GetDeltifiedCumulativeCost(deltifySize);
        writer.BareUInt64(_head.Serialize(), false);
        writer.BareUInt32(newCumulativeCost, false);
    }

    /// <summary>二阶段提交的后一阶段</summary>
    internal void UpdateDeltified(SizedPtr versionTicket, uint deltifySize) {
        uint newCumulativeCost = GetDeltifiedCumulativeCost(deltifySize);
        _head = versionTicket;
        _cumulativeCost = newCumulativeCost;
    }

    private readonly uint GetRebasedCumulativeCost(uint rebaseSize) => checked(rebaseSize + PerFrameOverhead);
    /// <summary>二阶段提交的前一阶段</summary>
    internal readonly void WriteRebase(IDiffWriter writer, uint rebaseSize) {
        uint newCumulativeCost = GetRebasedCumulativeCost(rebaseSize);
        writer.BareUInt64(_head.Serialize(), false);
        writer.BareUInt32(newCumulativeCost, false);
    }

    /// <summary>二阶段提交的后一阶段</summary>
    internal void UpdateRebased(SizedPtr versionTicket, uint rebaseSize) {
        uint newCumulativeCost = GetRebasedCumulativeCost(rebaseSize);
        _head = versionTicket;
        _cumulativeCost = newCumulativeCost;
    }

    /// <summary>通用于 rebase frame 和 deltify frame 。</summary>
    /// <param name="reader"></param>
    /// <returns>IsRebased</returns>
    /// <exception cref="Exception"></exception>
    internal void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket) {
        uint newCumulativeCost = reader.BareUInt32(false);
        _head = parentTicket;
        _cumulativeCost = newCumulativeCost;
    }
}
