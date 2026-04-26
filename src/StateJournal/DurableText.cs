using System.Diagnostics;
using Atelia.Data;
using Atelia.StateJournal.Internal;
using Atelia.StateJournal.NodeContainers;
using Atelia.StateJournal.Serialization;

namespace Atelia.StateJournal;

/// <summary>
/// 带稳定 block ID 的持久化文本容器。
/// 每个 block 拥有一个创建时分配的 <c>blockId</c>（单调递增不复用），
/// 编辑操作通过 blockId 寻址，而非行号或内容匹配。
/// </summary>
/// <remarks>
/// <para>设计动机：LLM Agent 的编辑意图天然是结构化的（"删除那个函数"、"在那行后面插入"），
/// 但传统文本编辑工具强迫 LLM 用内容复述来定位。稳定 ID 消除了这一降级。</para>
/// <para>底层基于 <c>LeafChainStore</c> 的单向链 + 增量序列化，
/// 不需要 Rope 或 PieceTree 等复杂结构。</para>
/// </remarks>
public sealed class DurableText : DurableObject {
    private TextSequenceCore _core = new();
    private VersionChainStatus _versionStatus;

    internal static readonly byte[] s_typeCode = [(byte)TypeOpCode.PushText];
    private protected override ReadOnlySpan<byte> TypeCode => s_typeCode;

    internal DurableText() { }

    public override DurableObjectKind Kind => DurableObjectKind.Text;
    public override bool HasChanges => _core.HasChanges;

    /// <summary>块数。</summary>
    public int BlockCount => _core.Count;

    /// <summary>获取指定 block 的内容。</summary>
    public TextBlock GetBlock(uint blockId) => new(blockId, _core.GetBlock(blockId));

    /// <summary>按链序返回所有 block。</summary>
    public IReadOnlyList<TextBlock> GetAllBlocks() => _core.GetAllBlocks();

    /// <summary>从指定 block 开始，按链序返回最多 <paramref name="maxCount"/> 个 block。</summary>
    public IReadOnlyList<TextBlock> GetBlocksFrom(uint startBlockId, int maxCount)
        => _core.GetBlocksFrom(startBlockId, maxCount);

    /// <summary>在指定 block 之后插入，返回新 blockId。</summary>
    public uint InsertAfter(uint afterBlockId, string content) {
        ThrowIfDetached();
        return _core.InsertAfter(afterBlockId, content);
    }

    /// <summary>在指定 block 之前插入，返回新 blockId。</summary>
    public uint InsertBefore(uint beforeBlockId, string content) {
        ThrowIfDetached();
        return _core.InsertBefore(beforeBlockId, content);
    }

    /// <summary>在队首插入一个 block，返回新 blockId。</summary>
    public uint Prepend(string content) {
        ThrowIfDetached();
        return _core.Prepend(content);
    }

    /// <summary>在队尾插入一个 block，返回新 blockId。</summary>
    public uint Append(string content) {
        ThrowIfDetached();
        return _core.Append(content);
    }

    /// <summary>替换指定 block 的内容。</summary>
    public void SetContent(uint blockId, string newContent) {
        ThrowIfDetached();
        _core.SetContent(blockId, newContent);
    }

    /// <summary>删除指定 block。</summary>
    public void Delete(uint blockId) {
        ThrowIfDetached();
        _core.Delete(blockId);
    }

    /// <summary>批量加载 block。仅空文本可用。</summary>
    public void LoadBlocks(ReadOnlySpan<string> lines) {
        ThrowIfDetached();
        _core.LoadBlocks(lines);
    }

    /// <summary>便捷方法：按换行符拆分加载。仅空文本可用。</summary>
    public void LoadText(string text) {
        // 按 \n 分割为块，兼容 \r\n（\r 保留在块内容中由调用方处理，
        // 或在渲染时统一）
        var lines = text.Split('\n');
        LoadBlocks(lines);
    }

    internal override SizedPtr HeadTicket => _versionStatus.Head;
    internal override bool IsTracked => _versionStatus.IsTracked;
    internal override ObjectVersionFlags VersionObjectFlags => _versionStatus.ObjectFlags;

    internal override void OnCommitSucceeded(SizedPtr versionTicket, DiffWriteContext context) {
        ObjectVersionFlags objectFlags = CurrentObjectFlags;
        if (context.WasRebase) {
            _versionStatus.UpdateRebased(versionTicket, context.EffectiveRebaseSize, objectFlags);
        }
        else {
            _versionStatus.UpdateDeltified(versionTicket, context.EffectiveDeltifySize, objectFlags);
        }
        _core.Commit();
        ClearCommittedPersistenceFlags();
        SetState(DurableState.Clean);
    }

    internal override FrameTag WritePendingDiff(BinaryDiffWriter writer, ref DiffWriteContext context) {
        Debug.Assert(context.FrameSource != FrameSource.Blank, "FrameSource must be explicitly set");

        // rebase frame 写 WriteBytes(TypeCode)，deltify frame 写 WriteBytes(null)；二者实际写出的字节都包含 VarUInt 长度前缀。
        uint rebaseSize = checked(_core.EstimatedRebaseBytes() + CostEstimateUtil.WriteBytesSize(TypeCode));
        uint deltifySize = checked(_core.EstimatedDeltifyBytes() + CostEstimateUtil.WriteBytesSize(default));
        bool doRebase = context.ForceRebase || _versionStatus.ShouldRebase(rebaseSize, deltifySize);
        if (doRebase) {
            context.SetOutcome(wasRebase: true, rebaseSize, deltifySize);
            writer.WriteBytes(TypeCode);
            _versionStatus.WriteRebase(writer, rebaseSize, CurrentObjectFlags);
            _core.WriteRebase(writer, context);
            return new(VersionKind.Rebase, Kind, context.FrameUsage, context.FrameSource);
        }

        context.SetOutcome(wasRebase: false, rebaseSize, deltifySize);
        writer.WriteBytes(null);
        _versionStatus.WriteDeltify(writer, deltifySize, CurrentObjectFlags);
        _core.WriteDeltify(writer, context);
        return new(VersionKind.Delta, Kind, context.FrameUsage, context.FrameSource);
    }

    internal override void ApplyDelta(ref BinaryDiffReader reader, SizedPtr parentTicket) {
        AssertReconstructionOnlyState();
        _versionStatus.ApplyDelta(ref reader, parentTicket);
        _core.ApplyDelta(ref reader);
    }

    internal override void OnLoadCompleted(SizedPtr versionTicket) {
        _versionStatus.SetHead(versionTicket);
        ApplyLoadedObjectFlags(_versionStatus.ObjectFlags);
        if (IsFrozen) { throw new InvalidDataException("Frozen DurableText is not supported by this implementation."); }
        _core.SyncCurrentFromCommitted();
        SetState(DurableState.Clean);
    }

    internal override void DiscardChanges() => _core.Revert();

    internal override void AcceptChildRefVisitor<TVisitor>(ref TVisitor visitor) {
        _core.AcceptChildRefVisitor(Revision, ref visitor);
    }

    internal override AteliaError? ValidateReconstructed(
        LoadPlaceholderTracker? tracker, Pools.StringPool? _) {
        if (tracker is null) { return null; }
        return _core.ValidateReconstructed(tracker, "DurableText");
    }
}
