using System.Collections.Generic;

namespace Atelia.MemoTree;

/// <summary>
/// 渲染 Window index 区的输入。
/// </summary>
public sealed record MemoTreeIndexRenderInput(
    MemoTreeRenderRequest Request,
    IReadOnlyList<MemoTreeIndexEntry> Entries
);

/// <summary>
/// 渲染单个 node card 的输入。
/// </summary>
public sealed record MemoTreeNodeCardRenderInput(
    MemoTreeRenderRequest Request,
    MemoNodeSnapshot Node,
    MemoNodePath Path,
    MemoNodeViewLevel ViewLevel,
    string? BodyText = null,
    IReadOnlyList<MemoBodyBlockSnapshot>? BodyBlocks = null,
    bool WasAutoCollapsed = false
);

/// <summary>
/// Window 的 index 区渲染器。
/// </summary>
/// <remarks>
/// 该渲染器只关心结构骨架如何投影，不负责决定哪些节点进入 flatten 区。
/// </remarks>
public interface IMemoTreeIndexRenderer {
    MemoTreeIndexSection RenderIndex(MemoTreeIndexRenderInput input);
}

/// <summary>
/// Window 的单节点卡片渲染器。
/// </summary>
/// <remarks>
/// 该渲染器只关心某个已选中的节点如何投影成 node card，不负责整体排序与拼接。
/// </remarks>
public interface IMemoTreeNodeCardRenderer {
    MemoTreeNodeCard RenderNodeCard(MemoTreeNodeCardRenderInput input);
}
