# TextAdv2 地点建模与移动逻辑重构提案

> 状态：提案
> 范围：`prototypes/TextAdv2/` 内与地点、Passage、导航观察、路线规划、角色移动 trace 相关的设计
> 目标：在不引入兼容负担的前提下，进一步收口当前设计中的重复投影、语义歧义与派生数据散落问题

本文不是实现任务清单，而是偏设计约束的重构方案。
重点不是“怎样最少改动”，而是“怎样把 TextAdv2 的空间语义边界收得更漂亮、更稳”。

---

## 1. 当前设计中值得继续保留的部分

先明确：`TextAdv2` 当前本体并不凌乱，反而已经有一条很好的主线，下面这些判断建议继续保留：

- `WorldTruth` 只保存 authoritative truth，不把邻接缓存、显示文案投影、算法缓存混进真相层
- `Passage` 把 shared / endpoint-local / direction-specific 事实分开，避免双端复制事实
- `Runtime` 承担 process-local state，`WorldHost` 承担 durable world commit boundary，这个分层方向是对的
- `Observation` 与 `Routing` 已经被视为从 world truth 派生的读模型，而不是第二份真相

因此本提案不是推翻现状，而是把现有方向进一步收口。

---

## 2. 核心设计判断

这次重构建议围绕一个中心思想展开：

**把“地点相邻关系及其方向化移动语义”收口为单一 canonical derived seam，再让 observation、routing、actor context 都从这条 seam 投影。**

这会同时解决三个问题：

- 消除多处全表扫描与重复邻接投影
- 把 session-local route trace 的语义边界说清楚
- 让 actor context 不再通过两套 projector 拼装同一份可行动面

换句话说，当前问题不在 `WorldTruth`，而在“WorldTruth 之上的派生层还不够统一”。

---

## 3. 目标设计

建议把空间相关设计收口为下面四层：

1. `WorldTruth`
   - 只保存 `Location` / `Passage` / `Actor` / logical time 这些 authoritative truth
   - 不保存邻接缓存、路线加速快照、session trace

2. `Derived Spatial Snapshot`
   - 从 `WorldState` 一次性派生出“按 location 索引的方向化邻接视图”
   - 它是只读派生结构，不是 durable truth
   - 它是 observation、planner、heuristic、authoring 校验共享的 canonical seam

3. `Read Models`
   - `LocationObservation`
   - `LocationNavigationObservation`
   - `ActorContextObservation`
   - `LocationRoutePlanObservation`
   - 这些都应从同一个 `Derived Spatial Snapshot` 投影，而不是彼此再转来转去

4. `Runtime Session State`
   - 只保存 process-local、可丢失的运行态，例如 route acceleration cache、session movement trace
   - 所有这类能力都必须在命名上显式暴露“session-local / runtime-local”边界

这个层次里，真正需要新增的不是更多 domain object，而是一个收口良好的派生 seam。

---

## 4. 方案一：收口为单一 `Derived Spatial Snapshot`

### 4.1 设计目标

当前系统里，“从某地点出发能沿哪些方向走出去、每条边的代价和语义是什么”这件事，在多个地方被重复计算：

- `LocationObservation`
- `NavigationObservation`
- route planner
- landmark heuristic precompute
- exit-name uniqueness 校验

它们都在消费同一类事实，但没有共用同一个派生结构。

建议引入一个内部只读 seam，名称可类似：

- `WorldSpatialSnapshot`
- `LocationAdjacencySnapshot`
- `DerivedWorldNavigation`

名称不重要，关键是语义边界必须清楚：

- 输入：`WorldState`
- 输出：按 `LocationId` 组织的方向化邻接结果
- 性质：只读派生、可重建、非 durable、非 authoring DTO、非 host contract

### 4.2 这个 seam 应该承载什么

建议它的核心单元是“从某 location 看出去的一条 direction-ready adjacency edge”，字段应以“共享一份、足够所有上层复用”为准。

建议至少包含：

- `PassageId`
- `FromLocationId`
- `ToLocationId`
- `ExitName`
- `TravelMode`
- `BaseTravelCost`
- `TravelCostModifier`
- `TotalTravelCost`
- `SharedConditionNote`
- `DirectionalConditionNote`
- `LocalViewNote`
- `IsEnabled`

也就是，它应更接近今天 `ExitObservation` 的事实密度，而不是更接近今天 `LocationNavigationGraphEdge` 的极简形状。

原因很简单：

- 从丰富边投影成轻量边很容易
- 反过来从轻量边恢复 rich observation 往往又要回 world 反查一次

因此 canonical seam 应偏“足够丰富的一次派生”，而不是“极瘦导致各层反复补查”的派生。

### 4.3 它不应该承载什么

这个 seam 仍不应该承载：

- 地点描述文案本体
- actor presence
- route-plan result
- session movement history
- landmark heuristic table 本身

它只回答一个问题：

**当前 world truth 下，location graph 的方向化边语义是什么。**

### 4.4 它应该放在哪一层

建议放在 `Routing` 或新增一个更中性的内部命名空间，例如：

- `Atelia.TextAdv2.Derived`
- `Atelia.TextAdv2.Spatial`

不建议放进 `WorldTruth`，因为它不是真相。
也不建议继续让 `Observation` 单独持有它，因为 planner 和 heuristic 也依赖同一层事实。

### 4.5 这个 seam 应如何被复用

重构后建议变成：

- `LocationObservationProjector` 从它投影 rich exits
- `NavigationObservationProjector` 从它投影 enabled-only lightweight edges
- `LocationRoutePlanner` 直接消费它的 enabled edges
- `LocationLandmarkHeuristicSnapshot` 直接从它构图
- `EnsureExitNameAvailable` 也从它读“某地点已有的 exit names”

这样可以把今天分散在多处的“按地点扫全图找 passage”统一消掉，同时仍然不污染 world truth。

### 4.6 生命周期建议

这个 seam 有两种都合理的落点：

- 方案 A：按次构建，不缓存
- 方案 B：在 `WorldRuntime` 内按 graph signature 持有一份 ephemeral snapshot cache

从当前项目阶段看，建议优先采用：

**先收口 seam，再决定是否缓存。**

也就是先把“逻辑来源唯一化”做对，再看是否需要把它和 `RouteAccelerationCache` 一起挂到 runtime 内做按签名复用。

如果后续要缓存，也应缓存“derived snapshot”，而不是让每个 projector/planner 各自缓存自己的局部结果。

---

## 5. 方案二：把 route trace 明确降格为 `Runtime Session Trace`

### 5.1 当前问题不是“trace 不持久化”

当前真正的问题不是 trace 没持久化，而是：

- API 名称和结果形状容易让人误以为它在描述 actor 的真实历史
- 但它实际上只是单进程 runtime 内记录到的移动序列
- reopen / host restart / reset 后清空是设计事实，却没有被 public contract 清晰表达

这会让调用方把 runtime debug seam 误当成 durable seam。

### 5.2 建议的设计结论

建议明确采纳下面的边界：

**TextAdv2 当前不存在 durable movement history。**

当前存在的只有：

- authoritative current location
- runtime-session-local movement trace

如果未来真的需要 durable 历史，应单独设计新的 event/journal seam，而不是把当前 trace 半升级为 durable。

### 5.3 命名重构建议

建议把命名整体改为显式 session 语义，例如：

- `TraceActorRoute` -> `TraceActorSessionRoute`
- `ActorRouteTrace` -> `ActorSessionRouteTrace`
- `ActorRouteTraceStep` -> `ActorSessionRouteTraceStep`

如果觉得 “Session” 太偏 host，也可以用：

- `RuntimeRouteTrace`
- `ActorRuntimeRouteTrace`

无论选哪组名字，原则都一样：

**类型名必须让人一眼知道它不是 world truth。**

### 5.4 结果里应补的边界字段

建议结果中显式带上：

- `RuntimeEpochId`
- 可选的 `RecordedStepCount`

其中 `RuntimeEpochId` 很关键。
它把“这是某次 runtime 生命周期内的 trace”从注释提升到结构化事实。

这样即使以后 `GameServer` 和 `E2eCli` 都暴露这个 seam，调用方也更难把不同 runtime 里的 trace 混在一起理解。

### 5.5 Contract 定位建议

文档层面建议明确：

- 它是 human-debug / dev-support seam
- 默认不属于 canonical machine surface
- 默认不属于 durable contract
- parity guard 即使存在，也只能守住“同一 runtime 语义下的输出形状”，不能把它包装成 durable 历史能力

这不是削弱能力，而是避免语义漂移。

---

## 6. 方案三：把 `ActorContextObservation` 改成单一 projector 产物

### 6.1 当前问题

`ActorContextObservation` 现在是拼装出来的：

- 先读 `ObserveActor`
- 再读 `ObserveActorNavigation`
- 再丢掉 `LocationObservation.Exits`

这在功能上可用，但设计上不够干净，因为它暗示：

- actor-facing context 并不是第一类读模型
- “当前地点信息”与“当前可行动面”来自两套平行 projector

于是任何关于“actor 此刻看到什么、能做什么”的语义收口，都容易分散到两处。

### 6.2 建议的设计结论

建议把 `ActorContextObservation` 视为第一类 read model，并给它单独 projector，例如：

- `ActorContextProjector`
- `ActorContextObservationProjector`

它应直接从以下输入一次性投影：

- actor truth
- current location truth
- current logical time
- derived spatial snapshot 中该 location 的 adjacency edges
- current location present actors

然后一次性产出：

- actor identity
- current tick
- narrow current location context
- canonical `availableMoves`

### 6.3 设计原则

这里建议明确两条规则：

1. `availableMoves` 仍然是唯一 canonical actor-facing action surface
2. `currentLocation` 永远不回退为“偷偷塞一份 exits”

也就是说，`ActorContextObservation` 不是 `LocationObservation` 的删减版，也不是 `ActorNavigationObservation` 的包壳。
它应是一个有自己边界的独立 contract。

### 6.4 与其他 observation 的关系

建议整理为下面的职责：

- `LocationObservation`
  - 回答“这个地点整体看起来是什么样，出口和在场角色有哪些”
- `LocationNavigationObservation`
  - 回答“从这个地点当前有哪些可通行边”
- `ActorContextObservation`
  - 回答“这个 actor 此刻处于什么上下文，并且可执行哪些移动动作”

三者都可以共享同一个 derived spatial snapshot，但不应再通过互相套壳的方式构造。

这会让 read model 的边界更稳定，也让后续增加 actor-specific visibility / action gating 时有明确落点。

---

## 7. 组合后的整体结构

把三个问题一起收口后，建议形成下面这条主链：

`WorldState`
-> `Derived Spatial Snapshot`
-> `LocationObservationProjector`
-> `NavigationObservationProjector`
-> `ActorContextProjector`
-> `LocationRoutePlanner`
-> `LocationLandmarkHeuristicSnapshot`

同时，运行态分出另一条旁路：

`WorldRuntime`
-> `RouteAccelerationCache`
-> `Runtime Session Trace`

这两条链的区别应非常鲜明：

- 上面那条是“世界真相的派生读模型链”
- 下面那条是“runtime 内部附加状态链”

一旦边界清楚，很多小问题都会自然消失：

- adjacency 不再到处重复投影
- actor context 不再靠拼装
- route trace 不再被误读为 durable truth

---

## 8. 建议的实施顺序

实现顺序不是本文重点，但为了降低重构风险，建议采用下面的收口顺序：

1. 先引入 `Derived Spatial Snapshot`，不改 public contract
   - 让 `NavigationObservationProjector`、planner、landmark heuristic 先切过去

2. 再让 `LocationObservationProjector` 和 exit-name availability 校验切到同一 seam
   - 到这一步，空间派生事实来源就已经统一

3. 新增 `ActorContextProjector`
   - 让 `ObserveActorContext` 改为一次性投影

4. 最后重命名 session trace 相关 API 与 DTO
   - 同时补文档，把它明确标成 runtime-session seam

这条顺序的好处是：

- 先统一事实来源
- 再收口 read model
- 最后再修 contract 命名

不会把多个层次的问题揉成一次大改。

---

## 9. 非目标

这次重构不建议顺手做下面这些事：

- 不把邻接索引持久化进 `WorldTruth`
- 不把 route trace 升级成 durable history
- 不为了“纯粹”而新增大量 adapter / abstraction layer
- 不把 `LocationObservation` 与 `NavigationObservation` 合并成单一 public DTO

这些动作要么会污染真相层，要么会把当前已经不错的边界重新搅混。

---

## 10. 最终建议

如果只保留一句话，本文的建议是：

**不要继续在多个 projector 和算法里各自“从 world 临时算一遍邻接关系”；应当建立单一的 derived spatial seam，并把 actor context 与 session trace 的语义边界一起收紧。**

对当前 `TextAdv2` 来说，这是一条“少加抽象、但让抽象更准”的重构路径。
它不会改变你现在已经做对的主结构，却能显著减少后续地点/移动系统继续长大时的边界漂移。
