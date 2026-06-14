using System.Collections.Generic;

namespace Atelia.MemoTree;

/// <summary>
/// 一份可编辑的 MemoTree 工作会话。
/// </summary>
public interface IMemoTreeSession {
    MemoTreeSnapshot Snapshot { get; }

    IReadOnlyList<MemoNodeId> GetRootNodes();

    IReadOnlyList<MemoNodeId> GetChildren(MemoNodeId nodeId);

    MemoNodePath GetPath(MemoNodeId nodeId);

    bool TryGetNode(MemoNodeId nodeId, out MemoNodeSnapshot? node);

    string GetBodyText(MemoNodeId nodeId);

    IReadOnlyList<MemoBodyBlockSnapshot> GetBodyBlocks(MemoNodeId nodeId);

    MemoNodeId CreateRoot(
        MemoHeadingLevel headingLevel,
        string title,
        string? impression = null,
        int? insertIndex = null
    );

    MemoNodeId CreateChild(
        MemoNodeId parentId,
        MemoHeadingLevel headingLevel,
        string title,
        string? impression = null,
        int? insertIndex = null
    );

    void MoveSubtree(MemoNodeId nodeId, MemoNodeId? newParentId, int? insertIndex = null);

    void DeleteSubtree(MemoNodeId nodeId);

    void SetTitle(MemoNodeId nodeId, string title);

    void SetImpression(MemoNodeId nodeId, string? impression);

    void SetSummary(MemoNodeId nodeId, string? summary, long basedOnContentVersion);

    void SetPinned(MemoNodeId nodeId, bool isPinned);

    void SetBodyText(MemoNodeId nodeId, string markdown);

    MemoBlockId AppendBodyBlock(MemoNodeId nodeId, string content);

    MemoBlockId InsertBodyBlockAfter(MemoNodeId nodeId, MemoBlockId afterBlockId, string content);

    void SetBodyBlockContent(MemoNodeId nodeId, MemoBlockId blockId, string content);

    void DeleteBodyBlock(MemoNodeId nodeId, MemoBlockId blockId);

    IReadOnlyList<MemoTreeSearchHit> Search(MemoTreeSearchQuery query);

    MemoTreeRenderResult Render(MemoTreeRenderRequest request);
}
