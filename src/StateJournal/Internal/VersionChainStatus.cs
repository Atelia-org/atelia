using System.Diagnostics;
using Atelia.Data;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal.Internal;

[Flags]
internal enum ObjectVersionFlags : uint {
    None = 0,
    Frozen = 1,
}

internal struct VersionChainStatus {
    // const uint ReadWeight = 1, WriteWeight = 1, StorageWeight = 1;
    const uint MaxReadAmplificationRatio = 3; // 来自于权重分配方案`Write:Read:Storage = 1:1:1`

    // PerFrameOverhead 拆解为各个独立可审阅的叶常量之和：
    //   - RBF 协议层：每帧固定开销 + 帧后 fence + 对齐 padding 的平均估算
    //   - VersionChain metadata 层：parentTicket / cumulativeCost / objectFlags 三个 BareUInt 字段的平均 VarInt 字节
    // 合计值是叶常量求和的副产品。修改协议（增删 metadata 字段、调整对齐策略等）时，
    // 应当只动对应叶常量，避免再次出现脱离协议事实的整数魔数。

    // RBF 帧固定开销：HeadLen(4) + PayloadCrc(4) + TrailerCodeword(16)。来自 RbfLayout.FixedOverhead。
    const uint RbfFrameFixedBytes = 24;
    // 写入路径在每帧 payload 之后追加的 fence。RbfLayout.FenceSize。
    const uint RbfFenceBytes = 4;
    // 帧对齐 padding 的平均估算（实际 0~3B，对齐分布近似均匀）。
    const uint AvgFramePaddingBytes = 2;
    // VersionChain metadata: parentTicket 的 BareUInt64（VarUInt）平均字节数。SizedPtr 紧凑交错编码后，常见 3~5B。
    const uint AvgParentTicketBytes = 4;
    // VersionChain metadata: cumulativeCost 的 BareUInt32（VarUInt）平均字节数。中小链上常见 2~3B。
    const uint AvgCumulativeCostBytes = 3;
    // VersionChain metadata: objectFlags 的 BareUInt32（VarUInt）平均字节数。当前仅用 Frozen=1 一位，恒为 1B。
    const uint AvgObjectFlagsBytes = 1;

    /// <summary>
    /// 单帧固定共享开销近似值，用于在 _cumulativeCost 中显式吸收 RBF 协议层与 VersionChain metadata 层的成本。
    /// 容器自身 payload 与 section count header 由各 EstimatedDeltifyBytes 精确建模，不在此处重复。
    /// </summary>
    const uint PerFrameOverhead =
        RbfFrameFixedBytes + RbfFenceBytes + AvgFramePaddingBytes
        + AvgParentTicketBytes + AvgCumulativeCostBytes + AvgObjectFlagsBytes;

    const ObjectVersionFlags KnownFlags = ObjectVersionFlags.Frozen;

    // 前一个 version 的 RBF Frame Ticket。
    // 注意：这里记录的是“逻辑前驱”，不要求一定与当前写入目标文件同源。
    // ExportTo/SaveAs 做 full rebase 到新文件时，会刻意保留旧 _head 作为跨文件祖先元数据。
    // 读取 rebase 头帧时，Load 会在看到非空 typeCode 后停止回溯，因此这个 parent 可以只是逻辑信息。
    SizedPtr _head;

    // 从头帧的 parentTicket 指向的基帧（含）到当前 _head（含）总共的开销。用于评估是否值得以deltify方式保存而非rebase。
    uint _cumulativeCost; // 由于PerFrameOverhead的存在，在初始化后_cumulativeCost不可能为0，可用于初始化检测。
    ObjectVersionFlags _objectFlags;

    internal readonly SizedPtr Head => _head;
    internal readonly bool IsTracked => _cumulativeCost != 0;
    internal readonly ObjectVersionFlags ObjectFlags => _objectFlags;
    internal readonly bool IsFrozen => (_objectFlags & ObjectVersionFlags.Frozen) != 0;

    internal readonly VersionChainStatus ForkForNewObject() {
        Debug.Assert(IsTracked, "Only committed/tracked objects can be forked.");
        return this;
    }

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
    internal readonly void WriteDeltify(BinaryDiffWriter writer, uint deltifySize, ObjectVersionFlags objectFlags) {
        uint newCumulativeCost = GetDeltifiedCumulativeCost(deltifySize);
        writer.BareUInt64(_head.Serialize(), false);
        writer.BareUInt32(newCumulativeCost, false);
        writer.BareUInt32((uint)objectFlags, false);
    }

    /// <summary>二阶段提交的后一阶段</summary>
    internal void UpdateDeltified(SizedPtr versionTicket, uint deltifySize, ObjectVersionFlags objectFlags) {
        uint newCumulativeCost = GetDeltifiedCumulativeCost(deltifySize);
        _head = versionTicket;
        _cumulativeCost = newCumulativeCost;
        _objectFlags = objectFlags;
    }

    private readonly uint GetRebasedCumulativeCost(uint rebaseSize) => checked(rebaseSize + PerFrameOverhead);
    /// <summary>
    /// 二阶段提交的前一阶段。
    /// 即使本次输出的是 rebase 帧，也会把当前 <see cref="_head"/> 写入 parentTicket，
    /// 以保留逻辑祖先链；这在 ExportTo/SaveAs 的跨文件 full snapshot 场景下是有意设计。
    /// </summary>
    internal readonly void WriteRebase(BinaryDiffWriter writer, uint rebaseSize, ObjectVersionFlags objectFlags) {
        uint newCumulativeCost = GetRebasedCumulativeCost(rebaseSize);
        writer.BareUInt64(_head.Serialize(), false);
        writer.BareUInt32(newCumulativeCost, false);
        writer.BareUInt32((uint)objectFlags, false);
    }

    /// <summary>二阶段提交的后一阶段</summary>
    internal void UpdateRebased(SizedPtr versionTicket, uint rebaseSize, ObjectVersionFlags objectFlags) {
        uint newCumulativeCost = GetRebasedCumulativeCost(rebaseSize);
        _head = versionTicket;
        _cumulativeCost = newCumulativeCost;
        _objectFlags = objectFlags;
    }

    /// <summary>通用于 rebase frame 和 deltify frame 。</summary>
    /// <param name="reader"></param>
    /// <returns>IsRebased</returns>
    /// <exception cref="Exception"></exception>
    internal void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket) {
        uint newCumulativeCost = reader.BareUInt32(false);
        ObjectVersionFlags objectFlags = ReadObjectFlags(ref reader);
        _head = parentTicket;
        _cumulativeCost = newCumulativeCost;
        _objectFlags = objectFlags;
    }

    private static ObjectVersionFlags ReadObjectFlags(ref BinaryDiffReader reader) {
        ObjectVersionFlags flags = (ObjectVersionFlags)reader.BareUInt32(false);
        ObjectVersionFlags unknown = flags & ~KnownFlags;
        if (unknown != 0) {
            throw new InvalidDataException($"Unsupported object flags 0x{(uint)unknown:X8}.");
        }
        return flags;
    }
}
