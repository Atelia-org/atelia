# TextAdv2 - Landmark Heuristic Near-Term Plan

> 状态：近期方案草案，Phase 0 已部分落地
> 适用范围：`prototypes/TextAdv2/ReadOnlyView/`、`prototypes/TextAdv2/AccelerationIndex/`
> 目标：在不要求 2D 几何真相的前提下，把当前 location-level 最短路从纯 Dijkstra 平滑推进到真正可扩展的 A*

## 一句话结论

TextAdv2 的首个真实 heuristic，近期 SHOULD 优先选择 **graph-based landmark lower bound**，而不是先把几何字段塞进 `Location`。

原因很直接：

- 它不要求坐标、中心点或 AABB。
- 它天然适配当前的 `Location` 节点 / `Passage` 有向边模型。
- 它天然支持单向边、非对称 travel cost、portal shortcut。
- 它更适合放在 `AccelerationIndex` 这种可丢弃、可重建的派生层里，而不是反向塑形 `WorldTruth`。

## 当前已落地

当前主线已经先把“可扩展接缝”落了下来：

- `LocationRoutePlanner` 不再把 heuristic 写死在内部，而是改为读取 `LocationRoutePlanningOptions`。
- 默认 heuristic 仍然是 `LocationRouteHeuristics.Zero`，因此现有行为和既有 Dijkstra 语义保持一致。
- `AccelerationIndex` 新增了 `LocationLandmarkHeuristicSnapshot`，作为第一版显式 landmark 集合的只读 lower-bound 快照。

也就是说，近期阶段已经不再需要先争论“以后要不要上 heuristic”；接下来的问题变成：

- 何时构建 landmark snapshot
- landmark 怎么选
- snapshot 何时需要重建
- 如何证明它确实给出了 non-negative lower bound

## 核心设计边界

三层边界在 landmark 方案里建议保持为：

| 层 | 责任 | 当前建议 |
|:---|:---|:---|
| `WorldTruth` | 世界唯一真相 | 不新增专为 heuristic 服务的字段 |
| `ReadOnlyView` | 路由规划与启发函数接口 | 持有 planner 与 `ILocationRouteHeuristic` seam |
| `AccelerationIndex` | 预计算下界、可丢弃快照 | 持有 landmark distances snapshot |

这条边界的核心含义是：

- planner 负责消费 heuristic，不负责发明 heuristic。
- heuristic snapshot 负责提供 lower bound，不负责成为新的业务真相。
- 地图如果完全没有几何语义，也仍然可以获得有效 heuristic。

## Directed ALT Lower Bound

当前图模型是 **有向、非负边权** 图，因此首版 landmark heuristic 应使用 directed ALT 的 lower bound，而不是只拿无向图直觉套过来。

对当前节点 $v$、目标节点 $t$、landmark 集合 $\mathcal{L}$，可使用：

$$
h(v, t) = \max\left(
0,
\max_{L \in \mathcal{L}} \left(d(L, t) - d(L, v)\right),
\max_{L \in \mathcal{L}} \left(d(v, L) - d(t, L)\right)
\right)
$$

其中：

- $d(L, x)$ 来自 landmark 到各节点的正向最短路距离。
- $d(x, L)$ 来自各节点到 landmark 的最短路距离，可通过反图上的 Dijkstra 预计算得到。

这套式子的优点是：

- 不依赖几何距离。
- 对单向边仍然正确。
- 对 portal 也仍然正确，因为它完全建立在图距离上，而不是欧氏直觉上。

## 为什么它比 AABB 更适合作为第一步

并不是 AABB heuristic 不好，而是它要求更强的前提。

如果未来世界本来就有明确的 2D 语义，那么 AABB 仍然 MAY 成为后续强化项；但在近期阶段，landmark 有几个明显优势：

- 不需要先决定 `Location` 是否应该携带几何真相。
- 不会被 portal 这类拓扑捷径直接击穿正确性。
- 和当前 `BaseTravelCost + TravelCostModifier` 的图代价模型完全同构。
- 可以先用显式 landmark 集合启动，不必等自动 landmark 选择器成熟。

## Snapshot 契约

`LocationLandmarkHeuristicSnapshot` 必须被当作一个 **只读快照**，而不是会自动自愈的活体索引。

近期阶段建议明确以下规则：

- 图边权 MUST 非负；若存在负权边，planner 与 landmark 预计算都应 fail fast。
- snapshot MUST 基于一个稳定世界图构建。
- 若以下任一事实发生变化，snapshot SHOULD 视为过期并重建：
  - passage enable/disable 状态变化
  - `BaseTravelCost` 变化
  - `TravelCostModifier` 变化
  - location / passage 拓扑变化
- 若某个 landmark 对某组节点没有可用距离，那个 landmark 对该次估值不贡献项，而不是猜一个正数。
- heuristic 实现拿不准时 MUST 返回 0，而不是返回未经证明的“看起来差不多”的距离。

换句话说，这套结构优先保证 correctness，再去换取性能收益。

## 近期实施顺序

### Phase 0：已落地

- 为 `LocationRoutePlanner` 引入最小 heuristic seam。
- 保持默认 zero heuristic，不破坏现有结果与文本快照。
- 引入显式 landmark 集合的 snapshot 实现。
- 用 route planner tests 验证：
  - 默认行为未变
  - landmark heuristic 不会改变最短路 correctness
  - target 本身若是 landmark，可得到精确 lower bound

### Phase 1：把 heuristic 从“存在”推进到“可观测”

下一步 SHOULD 补 planner 级统计信息，例如：

- expanded node count
- relaxed edge count
- frontier peak size
- final path cost
- heuristic name / landmark count

没有这些统计，就只能知道“能跑”，不知道 heuristic 是否真的带来了搜索收缩。

### Phase 2：显式 landmark 的选点约定

在自动选点之前，近期 SHOULD 先允许由 builder / demo world 显式声明 landmark 集合。

首版选点原则建议：

- 尽量选图上彼此分散的 location。
- 尽量覆盖不同 travel mode 主干区段。
- portal 两端 MAY 各放一个 landmark，以便更快压紧跨区域下界。
- 每个弱连通分量至少应有一个 landmark；否则该分量内可能长期只能退化到 0 heuristic。

### Phase 3：自动 landmark 选择

等显式 landmark 用顺之后，再考虑自动选点策略。近期可候选但不急着落地的方案包括：

- farthest-point sampling
- 多轮“最远点扩张”
- 按 connected component 分层选点
- 针对 portal / hub / choke point 的偏置选点

这一步的目标不是“找到理论最优 landmark 集”，而是用简单规则稳定产出明显优于 0 heuristic 的分散点集。

### Phase 4：生命周期与缓存边界

当 landmark snapshot 真正开始被频繁使用后，再进入下一层问题：

- snapshot 由谁持有
- 何时失效
- 是否允许 revision-keyed cache
- 是否需要对 demo / CLI 暴露“重建索引”命令

在这一步之前，不要急着把缓存生命周期复杂化。

## 近期非目标

以下内容当前 SHOULD 明确延后：

- actor-to-actor 追踪或会合 heuristic
- 几何坐标、中心点、AABB 真相建模
- 多层级 hierarchical pathfinding
- 自动处理“世界已变但 snapshot 未重建”的透明修复机制
- 针对超大地图的外存索引或分布式预计算

## 验收标准

近期 landmark 方向是否成立，应至少满足下面这些问题都能回答：

- 默认 zero heuristic 是否仍然让所有既有 route planner tests 原样通过？
- landmark heuristic 是否在 portal 存在时仍保持 admissible？
- 目标节点本身若属于 landmark 集合，是否能得到精确剩余代价？
- topology / cost 修改后，我们是否明确知道必须重建 snapshot，而不是继续静默复用旧表？
- 在加入搜索统计后，是否能看到比 zero heuristic 更少的 expanded nodes？

## 推荐的下一步

如果只做一个最值当的后续动作，我建议是：

1. 给 `LocationRoutePlanner` 补一份结构化搜索统计输出。
2. 先在测试地图和 demo world 上手动指定 2 到 4 个 landmark。
3. 用统计数据确认 landmark heuristic 是否真的压缩了搜索面。

做到这一步后，再决定是否需要自动选点、AABB 强化项或更复杂的层级路由。
