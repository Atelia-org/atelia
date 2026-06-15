# MemoTree Public API 草案

> 用途：为 `prototypes/MemoTree` 提供接口先行的起点，便于围绕 contract 做 TDD。

## 0. 设计目标

MemoTree 的 public API 先满足四件事：

1. 让上层把 MemoTree 当作“稳定节点 ID + 可编辑正文块”的 durable memo graph，而不是大字符串。
2. 让渲染与搜索成为显式能力，而不是零散 helper。
3. 让 `IApp` 适配层很薄，避免 Agent.Core 细节渗进树模型。
4. 暂时不把 StateJournal 的 repo/branch/commit 语义暴露为 public contract。

第四点是刻意的：当前要先把“树模型对不对、渲染预算对不对、工具边界对不对”做稳定，再决定 public API 是否需要直接暴露持久化生命周期。

## 1. API 分层

建议把 public API 分成三层：

### 1.1 树模型层

关注节点、路径、摘要版本、正文块与 `contains` 关系编辑。

核心入口：`IMemoTreeSession`

### 1.2 查询与投影层

关注两件事：

- 搜索：从长期记忆里找到相关节点
- 渲染：在预算内把节点树投影成 Window

这层仍由 `IMemoTreeSession` 直接提供能力，避免早期拆太碎。

### 1.3 Agent.Core 适配层

关注把 MemoTree 的 Window 渲染函数包装成 `IApp`。

核心入口：`MemoTreeApp`

## 2. 当前建议暴露的主要类型

| 类型 | 作用 |
|---|---|
| `MemoNodeId` | 稳定节点 ID |
| `MemoBlockId` | 正文 block ID |
| `MemoNodeViewLevel` | 三层展开级别 |
| `MemoNodeCollapseLevel` | 显式收起时的目标 LOD |
| `MemoTreeSnapshot` | 树级概要快照 |
| `MemoNodeSnapshot` | 单节点概要快照 |
| `MemoBodyBlockSnapshot` | 正文块快照 |
| `MemoNodePath` | 节点路径 |
| `MemoNodeCollapseRequest` / `MemoNodeCollapseResult` | 记忆维护与收起模型 |
| `MemoTreeSearchQuery` / `MemoTreeSearchHit` | 搜索模型 |
| `MemoTreeRenderRequest` / `MemoTreeRenderResult` | 渲染模型 |
| `IMemoTreeSession` | 树模型与查询/渲染主入口 |
| `MemoTreeApp` | `IApp` 包装器 |

## 3. 为什么主入口叫 IMemoTreeSession

这里选 `Session` 而不是 `Document` / `Store` / `Repository`，是因为它更贴近当前阶段的真实语义：

- 它代表一份可编辑的 MemoTree 工作会话。
- 它不承诺底层一定是文件、数据库或 StateJournal branch。
- 它当前公开的是 durable memo graph 的 v0 `contains` 投影，而不是把 Markdown 或 StateJournal 细节暴露出去。
- 它把树结构当唯一真相；heading level 只在渲染时由 depth 推导。
- 它允许未来把持久化生命周期放到更外层，而不破坏当前契约。

## 4. 草案中的关键方法

### 4.1 结构读取

- `Snapshot`
- `GetRootNodes()`
- `GetChildren(nodeId)`
- `GetPath(nodeId)`
- `TryGetNode(nodeId, out node)`

### 4.2 正文读取

- `GetBodyText(nodeId)`
- `GetBodyBlocks(nodeId)`

这里同时提供“整段读取”和“block 读取”，是为了兼顾：

- 简单断言与简单工具调用
- 增量编辑与稳定引用

### 4.3 树结构编辑

- `CreateRoot(...)`
- `CreateChild(...)`
- `MoveSubtree(...)`
- `DeleteSubtree(...)`

这里不再要求调用方提供 `headingLevel`。树结构是唯一真相，heading level 只在渲染时由 depth 推导。

### 4.4 节点内容编辑

- `SetTitle(...)`
- `SetImpression(...)`
- `SetSummary(..., basedOnBodyVersion)`
- `SetPinned(...)`

这里的 `Impression` 等价于节点的 `Gist` 文本；`Summary` 只概括本节点正文，不包含子节点内容。

`SetSummary` 显式要求 `basedOnBodyVersion`，是为了把摘要 stale 语义变成 contract，而不是实现细节。它适合作为低层更新入口。

### 4.5 正文编辑

- `SetBodyText(...)`
- `AppendBodyBlock(...)`
- `InsertBodyBlockAfter(...)`
- `SetBodyBlockContent(...)`
- `DeleteBodyBlock(...)`

这里同时保留粗粒度和细粒度入口。这样做的原因是：

- 测试里常常希望快速准备样例正文
- 真正的 agent 工具则更适合 block 级编辑

但 `SetBodyText(...)` 当前只是暂存的朴素入口，不应被理解为最终推荐主线。它与稳定 `blockId` 的关系尚未完全收口，后续大概率还会继续收缩或重命名。

### 4.6 显式收起与记忆维护

- `CollapseNode(request)`

`CollapseNode(...)` 是推荐给 Agent 的主线路径，用于处理“我刚看过 Full，现在决定收起到较低 LOD”这一动作。

它与直接 `SetSummary(...)` 的区别是：

- 它把“收起”与“记忆沉淀”绑定成一个显式动作
- 它要求同时提交新的 `Gist` 与 `Summary`
- 它显式带上 `basedOnBodyVersion`
- 它不把 renderer 的预算变化偷偷变成长期记忆写入

### 4.7 查询与渲染

- `Search(query)`
- `Render(request)`

把它们放在 session 上，而不是单独拆成 renderer/searcher service，目的是在早期保持调用路径短、对象图简单。

## 5. 当前故意不暴露的东西

当前草案刻意不把下面这些东西变成 public API：

- `Repository` / `Revision` / `CommitAddress`
- `DurableDict` / `DurableText` 的具体类型
- merge/rebase/branch 管理
- 自动摘要刷新调度器
- 相关性排序器和预算分配算法的内部分数细节

这些都更适合作为实现选择，而不是先锁成外部 contract。

## 6. 使用示例

```csharp
IMemoTreeSession tree = /* test double or future implementation */;

var project = tree.CreateRoot(
    "Project",
    impression: "当前主线与长期目标"
);

var stateJournal = tree.CreateChild(
    project,
    "StateJournal",
    impression: "持久化对象图与 commit 语义"
);

tree.SetSummary(
    stateJournal,
    "Repository 管 branch，Revision 管可编辑对象图，commit 从 root 遍历可达对象。",
    basedOnBodyVersion: 1
);

tree.SetBodyText(stateJournal, "- Repository.Create/Open\n- Revision.Create*\n- Commit(root)");
tree.SetPinned(stateJournal, true);

var collapsed = tree.CollapseNode(new MemoNodeCollapseRequest(
    NodeId: stateJournal,
    TargetLevel: MemoNodeCollapseLevel.Summary,
    Gist: "StateJournal 负责可持久化对象图工作态。",
    Summary: "Repository 管 branch，Revision 是工作会话，commit 从 root 遍历可达对象。",
    BasedOnBodyVersion: tree.TryGetNode(stateJournal, out var node) ? node!.BodyVersion : 0
));

var render = tree.Render(new MemoTreeRenderRequest(
    VisibleCharacterBudget: 2400,
    PreferredNodeIds: [stateJournal],
    IncludePinnedNodes: true,
    TopicHint: "StateJournal 用法"
));
```

上面的 `basedOnBodyVersion` 在真实使用中应来自 `TryGetNode(...).BodyVersion`。这里的示例重点只是说明调用形状。

`CollapseNode(...)` 是更符合 Agent 实际工作流的入口：它表示“我刚看过细节，现在准备把这个节点收起前顺手沉淀一下记忆”。

## 7. 适合最先写的 TDD 切片

1. 建树与顺序
2. nodeId 稳定性
3. move subtree 后路径变化
4. body block 增量编辑
5. `SetSummary` 与 `IsSummaryStale`
6. `CollapseNode` 同时更新 `Gist`、`Summary` 与可见目标 LOD
7. `Render` 在预算不足时优先压正文 LOD，而不是先抹掉结构骨架
8. `Pinned` 节点在紧预算下仍被优先保留
9. `MemoTreeApp` 只做薄适配，不篡改 `Window` 与 `HiddenToolNames`

## 8. 对应源码位置

当前草案对应的 contract 代码位于：

- `prototypes/MemoTree/MemoTree.Model.cs`
- `prototypes/MemoTree/IMemoTreeSession.cs`
- `prototypes/MemoTree/MemoTreeApp.cs`
