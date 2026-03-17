using System.Diagnostics;

namespace Atelia.StateJournal.Internal;

internal struct DiffWriteContext {
    /// <summary>最常见的写入上下文：用户负载 + 主提交。</summary>
    internal static readonly DiffWriteContext UserPrimary = new(FrameUsage.UserPayload, FrameSource.PrimaryCommit);

    internal DiffWriteContext(FrameUsage usage, FrameSource source) {
        FrameUsage = usage;
        FrameSource = source;
        AssertValid();
    }

    [Conditional("DEBUG")]
    internal void AssertValid() {
        Debug.Assert(
            FrameTag.IsValidUsage(FrameUsage),
            $"DiffWriteContext: FrameUsage 不合法，实际值为 {FrameUsage}，期望非 Blank。"
        );
        Debug.Assert(
            FrameTag.IsValidSource(FrameSource),
            $"DiffWriteContext: FrameSource 不合法，实际值为 {FrameSource}，期望非 Blank。"
        );
    }

    /// <summary>调用方可设为 true 以强制写出 rebase 帧（compact / 另存为新文件场景）。</summary>
    internal bool ForceRebase { get; init; }

    /// <summary>强制写入帧，即使对象本身无变更。
    /// 用于 ObjectMap 等场景：对象自身无变化但其层级上下文要求产生新帧。
    /// 与 ForceRebase 独立：rebase/delta 决策仍由 ShouldRebase 判定。</summary>
    internal bool ForceSave { get; init; }

    /// <summary>帧的用途（UserPayload / ObjectMap）。构造时必须显式指定，不可为 Blank。</summary>
    internal FrameUsage FrameUsage { get; init; }

    /// <summary>帧的来源（PrimaryCommit / Compaction / CrossFileSnapshot）。构造时必须显式指定，不可为 Blank。</summary>
    internal FrameSource FrameSource { get; init; }

    // WritePendingDiff 写入、OnCommitSucceeded 读取的决策结果。
    internal bool WasRebase { get; private set; }
    internal uint EffectiveRebaseSize { get; private set; }
    internal uint EffectiveDeltifySize { get; private set; }

    internal void SetOutcome(bool wasRebase, uint rebaseSize, uint deltifySize) {
        WasRebase = wasRebase;
        EffectiveRebaseSize = rebaseSize;
        EffectiveDeltifySize = deltifySize;
    }

    #region 暂时用不到，留给扩展点
    // // public List<ValueBox> ValueBoxTempList => field ??= new List<ValueBox>(); 目前的唯一的用户MixdDict不会用到
    // // public List<LocalId> LocalIdTempList => field ??= new List<LocalId>();
    // public List<Boolean> BooleanTempList => field ??= new List<Boolean>(); // `field`是C# 14的新关键字，用于访问属性自动生成的underlaying字段。
    // public List<String> StringTempList => field ??= new List<String>();
    // public List<Double> DoubleTempList => field ??= new List<Double>();
    // public List<Single> SingleTempList => field ??= new List<Single>();
    // public List<Half> HalfTempList => field ??= new List<Half>();
    // public List<UInt64> UInt64TempList => field ??= new List<UInt64>();
    // public List<UInt32> UInt32TempList => field ??= new List<UInt32>();
    // public List<UInt16> UInt16TempList => field ??= new List<UInt16>();
    // public List<Byte> ByteTempList => field ??= new List<Byte>();
    // public List<Int64> Int64TempList => field ??= new List<Int64>();
    // public List<Int32> Int32TempList => field ??= new List<Int32>();
    // public List<Int16> Int16TempList => field ??= new List<Int16>();
    // public List<SByte> SByteTempList => field ??= new List<SByte>();
    #endregion
}
