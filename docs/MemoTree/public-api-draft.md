# MemoTree Public API 草案

> 用途：为 `prototypes/MemoTree` 提供接口先行的起点，便于围绕 contract 做 TDD。
>
> 术语基线见：`docs/MemoTree/concepts-and-terminology.md`

## 0. 设计目标

MemoTree 的 public API 先满足四件事：

1. 让上层把 MemoTree 当作“稳定节点 ID + 可编辑正文块”的 durable memo graph，而不是大字符串。
2. 让渲染与搜索成为显式能力，而不是零散 helper。
3. 让 `IApp` 适配层很薄，避免 Agent.Core 细节渗进树模型。
4. 暂时不把 StateJournal 的 repo/branch/commit 语义暴露为 public contract。

同时，当前草案明确偏向一种长期任务友好的交互方式：

- Window 更像“index + flatten 节点卡片”的纯文本页面
- 节点本体采用 `Unified Node`：同一个节点可同时拥有正文与子节点
- `contains` 树是主导航，`tags` 只是可选次索引
- 默认维护动作是 block 级编辑与 `CollapseNode(...)`
- 整段正文重写只是危险辅助入口，不是主线路径

第四点是刻意的：当前要先把“树模型对不对、渲染预算对不对、工具边界对不对”做稳定，再决定 public API 是否需要直接暴露持久化生命周期。

## 1. API 分层

建议把 public API 分成三层：

### 1.1 树模型层

关注统一节点、路径、摘要版本、正文块、`contains` 关系与可选 tags。

核心入口：`IMemoTreeSession`

### 1.2 查询与投影层

关注两件事：

- 搜索：从长期记忆里找到相关节点
- 渲染：在预算内把节点树投影成“index + flatten 节点卡片”的 Window

这层仍由 `IMemoTreeSession` 直接提供能力，避免早期拆太碎。

### 1.3 Agent.Core 适配层

关注把 MemoTree 的 Window 渲染函数包装成 `IApp`。

核心入口：`MemoTreeApp`

## 2. 当前建议暴露的主要类型

| 类型 | 作用 |
|---|---|
| `MemoNodeId` | 稳定节点 ID |
| `MemoBlockId` | 正文 block ID |
| `MemoTag` | 节点上的轻量标签 |
| `MemoNodeViewLevel` | 三层展开级别 |
| `MemoNodeCollapseLevel` | 显式收起时的目标 LOD |
| `MemoTreeSnapshot` | 树级概要快照 |
| `MemoNodeSnapshot` | 单节点概要快照 |
| `MemoBodyBlockSnapshot` | 正文块快照 |
| `MemoBodyRewriteRequest` | 危险的整段正文重写请求 |
| `MemoNodePath` | 节点路径 |
| `MemoTreeIndexEntry` / `MemoTreeIndexSection` | Window 的 index 区 |
| `MemoTreeNodeCard` | Window 中平铺展示的单节点卡片 |
| `IMemoTreeIndexRenderer` | index 区渲染器 |
| `IMemoTreeNodeCardRenderer` | 单节点卡片渲染器 |
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
- 它把节点当作统一知识单元，而不是先拆成 directory/file 两种本体类型。
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

对 Agent 来说，推荐主视角仍应是 block 读取而不是“取整段再整段改写”。

### 4.3 树结构编辑

- `CreateRoot(...)`
- `CreateChild(...)`
- `MoveSubtree(...)`
- `DeleteSubtree(...)`

这里不再要求调用方提供 `headingLevel`。树结构是唯一真相，heading level 只在渲染时由 depth 推导。

### 4.4 节点内容编辑

- `SetTitle(...)`
- `SetGist(...)`
- `SetSummary(..., basedOnBodyVersion)`
- `SetTags(...)`
- `AddTag(...)`
- `RemoveTag(...)`
- `SetPinned(...)`

这里的 `Gist` 表示节点在最轻 LOD 下保留的一句话印象；`Summary` 只概括本节点正文，不包含子节点内容。

`SetSummary` 显式要求 `basedOnBodyVersion`，是为了把摘要 stale 语义变成 contract，而不是实现细节。它适合作为低层更新入口。

tags 当前只应被理解为轻量次索引：

- 适合横向聚合与过滤
- 不适合替代 `contains` 树成为主导航
- 不应在 v0 就膨胀成复杂查询语言

### 4.5 正文编辑

- `RewriteBodyText(request)`
- `AppendBodyBlock(...)`
- `InsertBodyBlockAfter(...)`
- `SetBodyBlockContent(...)`
- `DeleteBodyBlock(...)`

这里同时保留危险的粗粒度入口和推荐的细粒度入口。这样做的原因是：

- 测试里常常希望快速准备样例正文
- 真正的 agent 工具则更适合 block 级编辑

`RewriteBodyText(...)` 代表“我接受整段重写后果，准备从头替换这个节点正文”。它应被视为危险操作：

- 实现可以选择重建正文 block
- 旧 `blockId` 引用可能失效
- 更容易把局部修补偷换成全量覆盖

因此，它更适合初始化、导入、测试夹具与人工确认后的场景，不适合作为长期记忆维护主线。

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

如果已启用 tags，`Search(query)` 可以把 tags 当作轻量可搜索字段之一；但这仍不应改变“树是主导航、tags 是次索引”的基本分工。

当前对 `Render(...)` 的建议输出心智模型是：

- 先给一个稳定 index
- 再平铺当前相关节点的 node cards
- 节点卡片里再按 LOD 放 `Gist`、`Summary`、正文片段

实现上建议把两段逻辑拆开：

- `index renderer`：只关心树结构骨架如何投影
- `node card renderer`：只关心单个展开节点如何投影

MVP 阶段两者仍共同写入同一个 `IApp` Window，不必先拆成多 Window 布局。

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
    gist: "当前主线与长期目标"
);

var stateJournal = tree.CreateChild(
    project,
    "StateJournal",
    gist: "持久化对象图与 commit 语义"
);

tree.AddTag(project, new MemoTag("identity"));
tree.AddTag(stateJournal, new MemoTag("storage"));
tree.AddTag(stateJournal, new MemoTag("design"));

tree.SetSummary(
    stateJournal,
    "Repository 管 branch，Revision 管可编辑对象图，commit 从 root 遍历可达对象。",
    basedOnBodyVersion: 1
);

tree.RewriteBodyText(new MemoBodyRewriteRequest(
    stateJournal,
    "- Repository.Create/Open\n- Revision.Create*\n- Commit(root)",
    Reason: "seed sample body for an early prototype"
));
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
3. Unified Node 允许同一节点同时拥有正文与子节点
4. move subtree 后路径变化
5. body block 增量编辑
6. `RewriteBodyText` 被明确当作危险入口，而不是默认维护动作
7. `SetSummary` 与 `IsSummaryStale`
8. `tags` 只作为轻量次索引，不影响主结构稳定性
9. `CollapseNode` 同时更新 `Gist`、`Summary` 与可见目标 LOD
10. `Render` 先生成紧凑 index，再平铺 node cards
11. `Render` 在预算不足时优先压正文 LOD，而不是先抹掉结构骨架
12. `Pinned` 节点在紧预算下仍被优先保留，并主要体现在 node card 排序
13. `MemoTreeApp` 只做薄适配，不篡改 `Window` 与 `HiddenToolNames`

## 8. 对应源码位置

当前草案对应的 contract 代码位于：

- `prototypes/MemoTree/MemoTree.Model.cs`
- `prototypes/MemoTree/MemoTree.Rendering.cs`
- `prototypes/MemoTree/IMemoTreeSession.cs`
- `prototypes/MemoTree/MemoTreeApp.cs`
