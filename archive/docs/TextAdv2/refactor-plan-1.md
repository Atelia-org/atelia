# TextAdv2 渐进重构路线图

更新时间：2026-05-31

## 1. 背景

`prototypes/TextAdv2/` 当前已经形成了一个可运行的最小空间引擎，但也同时暴露出几条会互相放大复杂度的边界问题：

- `WorldTruth`、`ReadOnlyView`、`Runtime`、宿主层之间的职责还没有彻底收口。
- `Runtime` 里混入了渲染、命令分发、sample bootstrap、sidecar 持久化、route acceleration 生命周期等多类职责。
- 导航图语义散落在多份 read model / signature / heuristic 组装逻辑里，缺少单一派生真源。
- `WorldTruth` 已经显露出“结构化空间事实”和“视角化 prose”混放的趋势。

本项目当前没有旧 API / 旧数据兼容负担，因此本路线图优先选择“彻底化简并收口边界”，而不是保留兼容层。

## 2. 重构目标

目标不是一次性“重写 TextAdv2”，而是按工作包逐步把系统收成下面的形状：

- `WorldTruth`：只保存 authoritative、结构化、可验证的世界真相。
- `ReadOnlyView`：只做只读投影；导航相关派生结构共享单一 graph seam。
- `Runtime`：只做 typed use case / session orchestration，不承担宿主渲染协议。
- `GameServer` / `E2eCli`：各自拥有自己的命令面、渲染和 dev-only bootstrap。
- 派生缓存与调试态：要么明确是进程内易失状态，要么进入 repo 内一致性边界；不再留 repo 外的“半真相 sidecar”。

## 3. 设计原则

1. 单一真源优先于方便的重复投影。
2. 先收口对外契约，再收内部数据骨架，再收执行主链。
3. 对草稿态接口不留兼容层；若方向成立，直接迁移调用方。
4. 一包只解决一个主问题，但应补齐该问题自然 blast radius 内的尾修。
5. 测试以“当前单一真源”断言为主，不保留伪装成主断言的旧路径。

## 4. 当前现状校准

截至 2026-05-31，`TextAdv2` 的真实进度已经与最初路线图相比前进了不少，后续计划需要按当前代码而不是按旧假设来排：

- `P2 typed runtime seam` 已经覆盖：
  - `ObserveTime` / `AdvanceTime`
  - `ObserveRouteAcceleration` / `RebuildRouteAcceleration`
  - `ObserveLocation` / `ObserveActor`
  - `ObserveNavigation` / `ObserveActorNavigation`
  - `MoveActor`
  - `TraceActorRoute`
  - `PlanRoute` / `PlanActorRoute`
- `WorldSession` public surface 已不再暴露 `TextAdv2RuntimeCommandResult`。
  - `DumpWorld` / `DumpLocation` 已从 runtime 主对象移出。
  - `GameServer` / `E2eCli` 的 world/location 文本入口已统一改走显式 `DevTextRenderer.RenderWorld/RenderLocation(...)` helper。
- `P3` 已拆成多个更小的内部收口切口：
  - `schemaVersion` 字段已经写入 world root，且 `WorldState.FromRoot(...)` 已具备 schema gate；
  - `Passage` 高频写操作已通过 `WorldState.SetPassage*` + `PassageView` façade 收回 world authority，并已补 seam guard tests；
  - `Location` / `Actor` 仍保留叶子 setter，但当前几乎没有真实调用压力，已经不再是最近的关键路径阻塞。
- `P4` 实际上已经完成：
  - `LocationNavigationGraphProjector.Project(...)` 现在承载 canonical graph seam；
  - `NavigationObservationProjector` 与当前 planner、landmark heuristic、route-acceleration stale 判定都从这份 seam 读取，不再把图语义藏在展示 projector 内部。
- `P5` 的真实剩余问题需要按宿主边界重写：
- sample-world seed 与默认 landmark profile 已从 runtime public seam 下沉；
- `GameServer` 的 open/reset 策略已收口到显式 host policy，`E2eCli` 也已要求显式选择 `--repo-dir` 或 `--dev-sample-world`；
- `DumpWorld` / `DumpLocation` 的最终归属已定为显式 dev-support helper；runtime 不再保留这条调试文本 public surface。

## 5. 修订后的工作包顺序

### P0. 路线文档与施工约束

状态：已完成（2026-05-31）

目标：

- 固定重构顺序、写入边界、完成定义。

完成定义：

- 本文档落盘。
- 后续工作包都能引用本文作为高层设计约束。

### P1. 收掉 runtime sidecar 副真相

状态：已完成（2026-05-31）

主张：

- `logical time` 与 `movement history` 先降级为进程内调试态，不再持久化到 repo 外 sidecar。
- `movement result` 与 `movement history entry` 分离；历史只保留 trace 真正需要的轻量字段，不再嵌入 `LocationObservation`。

原因：

- 这是当前最明确的双真源和一致性裂缝。
- 该包能在不先重写宿主协议的情况下，显著降低后续演进复杂度。

写入范围：

- `prototypes/TextAdv2/Runtime/`
- `prototypes/TextAdv2/ReadOnlyView/ActorRouteTrace.cs`
- `tests/TextAdv2.Tests/` 中受影响测试

完成定义：

- 删除或停用 repo 外 runtime sidecar 持久化主路径。
- `TraceActorRoute` 仍可工作，但只依赖轻量 history。
- `OpenOrCreateRuntime` reopen 后只恢复 world truth，不再承诺恢复 time/history。
- 相关测试改为反映新的单一真源边界。

本轮落地结果：

- `logical time` 与 `movement history` 已降为进程内易失调试态。
- `movement history` 已与 `MoveActor` 返回 DTO 解耦，不再持有 `LocationObservation`。
- 旧 `.textadv2-runtime-state.json` 不再参与主路径恢复；仅残留该文件时会被视为 legacy 垃圾并清理。

### P2. 把 runtime 收回 typed use case 层

状态：已完成（2026-05-31）

主张：

- `WorldSession` 不再返回 `string + contentType`。
- runtime 只返回 typed result；文本渲染、JSON 序列化、命令解析分别下沉到 `GameServer` / `E2eCli`。
- `TextAdv2RuntimeCommand` 这类宿主导向命令适配器迁出 runtime 主对象。

原因：

- 这是宿主边界和内部草稿 API 的主收口点。
- 若不先做这一包，后面无论改 `WorldTruth` 还是改导航派生层，都会被当前输出格式反向绑住。

修订后的切口：

- P2a：删除 runtime command adapter（已完成，2026-05-31）。
- P2b1：迁出 time / route acceleration typed seam（已完成）。
- P2b2a：迁出 location / actor observation typed seam（已完成）。
- P2b2b：迁出 navigation observation typed seam（已完成）。
- P2b2c：只保留 `MoveActor` 这一条核心写入 use case 的 typed seam（已完成）。
- P2c1：删除 `MoveActorQuiet` 这类对 `MoveActor` 的文本别名，改由宿主本地渲染 compact movement（已完成，2026-05-31）。
- P2c2：为 `TraceActorRoute` 建 typed seam，文本渲染下沉到宿主或显式 dev support（已完成，2026-05-31）。
- P2c3：在 `DumpWorld` / `DumpLocation` 的最终归属明确后，再决定是否彻底删除 `TextAdv2RuntimeCommandResult`（已完成，2026-05-31）。

完成定义：

- `WorldSession` 的 public surface 不再暴露宿主渲染协议。
- CLI/HTTP 仍通过现有用例工作，但渲染责任在宿主项目。

P2a 本轮落地结果：

- `TextAdv2RuntimeCommand` / `Execute(...)` 已从 runtime 主对象删除。
- `HostingScaffold` 的状态说明已同步到" runtime 直接暴露 typed methods，宿主自己做调用分发"。
- 下一切口是 `P2b1`，不是继续扩大 `P2a`。

P2b1 本轮落地结果：

- `ObserveTime()` / `AdvanceTime(long)` 已直接返回 `LogicalTimeSnapshot`，由 `GameServer` / `E2eCli` 在宿主边界序列化。
- `ObserveRouteAcceleration()` / `RebuildRouteAcceleration(...)` 已直接返回 `RouteAccelerationSnapshot`，对应 HTTP/CLI 入口也已迁到宿主边界序列化。
- 对应的 runtime 测试，以及 time/route-acceleration 的 GameServer JSON 边界断言，已跟随迁移完成更新。
- 下一自然入口是 `P2b2`：为 actor/location/navigation/move 设计更干净的 public DTO seam，再继续缩减 `TextAdv2RuntimeCommandResult` 的残留范围。

P2b2a 本轮落地结果：

- `ObserveLocation(string)` 已直接返回 `LocationSnapshot`。
- `ObserveActor(string)` 已直接返回 `ActorSnapshot`。
- 新 seam 没有直接公开 `ReadOnlyView` DTO，也没有把 `TravelMode` 公开成 public enum；runtime 在边界前把 travel mode 投影成字符串 token。
- `GameServer` / `E2eCli` 对应入口已迁到宿主边界序列化，相关 runtime / GameServer 测试已更新到 typed 断言。
- 下一自然入口是 `P2b2b`：决定 navigation observation 是否沿用同类 runtime-facing DTO 策略，或与 `P4 canonical navigation graph seam` 一起收口。

P2b2b 本轮落地结果：

- `ObserveNavigation(string)` 已直接返回 `LocationNavigationSnapshot`。
- `ObserveActorNavigation(string)` 已直接返回 `ActorNavigationSnapshot`。
- 新 seam 没有直接公开 `ReadOnlyView.NavigationObservation*`，也没有把 `TravelMode` 公开成 public enum；runtime 继续在边界前把 travel mode 投影成字符串 token。
- `GameServer` / `E2eCli` 对应 navigation 入口已迁到宿主边界序列化，相关 runtime / GameServer 测试已更新到 typed 断言。
- 下一自然入口是 `P2b2c`：决定 `MoveActor` 是否也采用独立 runtime-facing DTO seam，或先转入 `P4 canonical navigation graph seam` 做内部图真源收口。

P2b2c 本轮落地结果：

- `MoveActor(string, string)` 已直接返回 `ActorMoveResult`。
- 新 seam 没有直接公开 internal `ActorMovementObservation`，并继续把 `TravelMode` 投影成字符串 token；`CurrentLocation` 复用 `LocationSnapshot`。
- `GameServer` move endpoint 与 `E2eCli --move-actor` 已迁到宿主边界 JSON 序列化，相关 runtime / GameServer 测试已更新到 typed 断言。
- `MoveActorQuiet`、`TraceActorRoute`、`PlanRoute`、`PlanActorRoute`、`Dump*` 仍保持现状，留待后续工作包收口。

P2c1/P2c2 本轮落地结果：

- `MoveActorQuiet` 已从 `WorldSession` 删除；`E2eCli --move-actor-quiet` 改为 `MoveActor(...) + DevTextRenderer.RenderCompactMovement(...)`。
- `TraceActorRoute(string)` 已直接返回 `ActorRouteTrace`；新 seam 没有公开 internal `ActorRouteTraceObservation`，并继续把 `TravelMode` 投影成 runtime-facing string token。
- route trace / compact movement 的文本渲染已移到显式 `DevTextRenderer`；`GameServer /actors/{actorId}/route-trace` 保持 `text/plain`，但文本现在在宿主边界渲染。
- 相关 runtime 测试已迁到 typed seam 主断言；文本 golden 已改到 renderer / host 边界测试。

P4c 本轮落地结果：

- `PlanRoute(string, string)` 与 `PlanActorRoute(string, string)` 已直接返回 `RoutePlan`。
- 新 seam 没有直接公开 internal `LocationRoutePlanObservation`，并继续把 `RoutePlanStatus` / `TravelMode` 投影成 runtime-facing string token。
- `GameServer` route-plan endpoints 与 `E2eCli --plan-route` / `--plan-actor-route` 已迁到宿主边界 JSON 序列化，相关 runtime / GameServer 测试已更新到 typed 断言。
- `LocationRoutePlanTextRenderer` 仍保留为内部文本调试能力；是否继续保留长期 text surface，留待 `P2c + P5c` 统一处理。

P2 后续修订说明：

- `MoveActor` 已完成 typed seam 收口；它不再是 `P2` 的残余问题。
- `TraceActorRoute` 已在 `P2c2` 中收口为 typed seam；后续不再属于 `TextAdv2RuntimeCommandResult` 的主残余面。
- `PlanRoute` / `PlanActorRoute` 已在 `P4c` 中收口为 typed seam；后续不再属于 `TextAdv2RuntimeCommandResult` 的主残余面。
- `MoveActorQuiet` 已删除，`compact movement` 文本改为显式 dev-support renderer；`TraceActorRoute` 文本也已下沉到宿主或显式 dev-support。
- `DumpWorld` / `DumpLocation` 已在 `P5c` 中收口到显式 `DevTextRenderer` bridge；`WorldDumpRenderer` 继续作为内部 canonical formatter，runtime public surface 不再保留 dump 文本 seam。
- `TextAdv2RuntimeCommandResult` 已删除；`SessionContentTypes` 暂保留给 `GameServer` 宿主边界复用。

### P3a. world root schema gate

状态：已完成（2026-05-31）

主张：

- `WorldState.FromRoot(...)` 对 `schemaVersion` 做 fail-fast。
- 错版本、缺版本、错误 kind 的 world root 应在 reopen 边界立即失败。

原因：

- 这是成本很低、收益很直接的小包。
- 当前 `schemaVersion` 字段已经落盘，但 reopen 入口没有设闸，处于“写了版本号却不验证”的半成品状态。

完成定义：

- `FromRoot(...)` 在 schema 不匹配时抛出清晰错误。
- 覆盖 happy path 与 bad-schema path 的测试。

本轮落地结果：

- `WorldState.FromRoot(...)` 已在 reopen 边界对 `schemaVersion` 做 fail-fast。
- 当前已覆盖 missing、unsupported version、invalid type、wrong kind 与 happy path reopen 测试。

### P3b. 收紧 world root 与写入权威

主张：

- `WorldTruth` 写操作逐步收回到 `WorldState` / 单一 world editor。
- 叶子对象默认朝只读 façade 收口。

原因：

- 这是未来扩展世界编辑约束、删除/改名/门禁规则的前提。

修订说明：

- 这一包不再和 schema gate 绑定。
- 当前最大的真实阻塞不是“要不要收权”，而是还没有明确的 `world editor` / `WorldState` 写接口来承接现有 leaf mutator。
- 这意味着它应排在 `P3a`、`P4`、以及至少一轮 sample/dev harness 下沉之后，再作为较大的内部收口包处理。

完成定义：

- world root 入口具备明确版本闸门。
- 新增或修改跨实体规则时，不需要在多个叶子对象上分散维护 invariant。

P3b.1 本轮落地结果：

- `WorldState` 已新增 `SetPassage*` world-level write seam，覆盖 travel mode、base cost、shared note、endpoint local view note、direction enabled、direction travel-cost modifier、direction condition note。
- `TestWorldBuilder` 与关键 route-acceleration / planner / world-builder tests 已迁移到这些 world-level API，不再把 `Passage.Set*` 当主写入口。
- `Passage.Set*` 已从 public 收紧为 internal，开始把 `Passage` 朝只读 facade 收口。

P3b.2 本轮落地结果：

- `WorldState.CreatePassage(...)`、`GetPassage(...)`、`TryGetPassage(...)`、`EnumeratePassages()`、`EnumeratePassagesTouching(...)` 已统一改为返回 distinct `PassageView` 只读 facade，不再把 concrete mutable `Passage` 暴露给主读链路。
- `WorldState` 内部 writable `Passage` 获取已收成 private helper，仅供 `SetPassage*` 与合法移动路径使用，没有新增对外可滥用的 mutable get 旁路。
- `ReadOnlyView`、`Runtime`、`WorldDumpRenderer` 与关键测试已迁到 facade，`Passage` concrete type 不再出现在这些主读链路里；并已补充 seam guard tests 锁住 `Create/Get/TryGet/EnumeratePassages*` 的只读返回面。

P3b 下一自然入口：

- `Location` / `Actor` 的叶子写口收权目前降为触发式后续项：等真正出现 world editor / rename / narrative editing 需求后，再按实际 invariants 设计后续切口。
- 在没有真实调用压力前，不把它排进最近几包的关键路径。

### P4. 建立 canonical navigation graph seam

状态：已完成（2026-05-31）

主张：

- 导航相关派生结构共享一份 canonical graph read model。
- `planner`、`landmark heuristic`、`graph signature`、调试观察都从这份 graph 读取。
- route acceleration 若没有真实性能压力，继续简化；必要时再保留显式缓存层。

原因：

- 当前图语义分散在多份 DTO 与派生逻辑里，是后续算法扩展的主要漂移源。
- 但它并不是“从零开始”的新想法；当前已经存在一个被多处复用、但尚未正式命名的 graph seam。

修订后的建议切口：

- P4a：把隐式 navigation graph seam 正式收口为 `LocationNavigationGraph` / `LocationNavigationGraphProjector`。
- P4b：让 planner / landmark heuristic / route-acceleration stale signature 显式改读这份 seam，而不是继续把它当作 projector 内部细节。
- P4c：在 graph seam 稳定后，再决定 `PlanRoute` / `PlanActorRoute` 的 public typed seam，以及是否保留显式 `Observe/RebuildRouteAcceleration` API。

完成定义：

- 图的 enabled/filter/cost 语义只定义一次。
- route acceleration 的 stale 判定不再拼接另一份手写图描述。

P4a/P4b 本轮落地结果：

- 隐式 graph seam 已正式收口为 `LocationNavigationGraph` / `LocationNavigationGraphProjector`。
- `NavigationObservationProjector` 已退回为展示层 adapter：先读 canonical graph，再补 `ExitName`、`TargetLocationName` 等展示字段。
- `LocationRoutePlanner`、`LocationLandmarkHeuristicSnapshot`、`TextAdv2RouteAcceleration` 的 stale-signature 计算已显式改读这份 seam，不再依赖 `NavigationObservationProjector` 的内部细节。
- 已补“同一次拓扑变更同时影响 navigation / planner / route-acceleration stale”回归测试，进一步锁住单一真源收口。
- `P4c` 也已完成；`Observe/RebuildRouteAcceleration` 的 public API 仍保持现状，留待后续按需要评估。

### P5. sample-world / dev harness 下沉

状态：已完成（2026-05-31）

主张：

- sample-world 创建、默认 landmark profile、open-or-create/reset、debug dump 这类开发态语义从 runtime 主对象退出。
- `GameServer` / `E2eCli` 只保留显式的 dev/admin 壳，不再让这些策略隐式穿过 runtime 主路径。

原因：

- 当前最大的真实问题不是“有没有 helper”，而是 runtime 仍直接依赖 `TestWorldBuilder` 和 sample-world policy。
- 这件事与 `MoveActor` seam、`P4` 都相对正交，适合拆成更具体、可单独验收的小包。

修订后的建议切口：

- P5a：把 sample-world profile / 默认 landmark policy 从 `WorldSession` 中剥离到显式 dev support 层（已完成）。
- P5b1：把 `GameServer` 的 open-existing / open-or-create / reset-sample-world 决策从 `TextAdv2RuntimeService` 中提出，改成显式 host bootstrap/admin policy（已完成）。
- P5b2：让 `E2eCli` 的“未指定 repoDir 就创建临时 sample world”成为显式 dev mode，而不是 runtime command 主路径的隐含行为（已完成）。
- P5c：处理 `DumpWorld` / `DumpLocation` 等稳定调试文本输出的最终归属；若这一步完成后 `TextAdv2RuntimeCommandResult` 已无剩余，再彻底删除它。

完成定义：

- runtime 不再直接依赖 `TestWorldBuilder`、默认 landmark profile、或 open-or-create/reset 语义。
- 宿主层对哪些接口是 dev-only 有清晰边界。

P5a 本轮落地结果：

- sample-world world seed 已从 `WorldSession` 中移出，改由 `SampleWorldBootstrap` 显式持有。
- `RebuildRouteAcceleration` 的"无参/`default` => 推荐 profile"决策已从 runtime public seam 移到 `SampleWorldBootstrap`，宿主仍保留原有 dev/admin 行为，但 runtime 本体只接受显式 landmark 请求。
- 新增回归测试，明确区分“通过 dev bootstrap 打开的 runtime 可以走 sample-world 默认 landmark profile”与“直接 `OpenExisting(...)` 的 runtime 不应隐式拥有该 policy”。
- `GameServer` 已把 runtime handle 与 host bootstrap/admin policy 分离：`TextAdv2RuntimeService` 只保留持有/替换 runtime，sample-world dev open/reset 与 repo lock retry 都回到 host-local policy；`runtime-status`/`plannedEndpoints` 也已显式暴露这层边界。
- 下一自然入口收窄为 `P5b2`。

P5b2 本轮落地结果：

- `TextAdv2.E2eCli` 已新增显式 dev mode 旗标 `--dev-sample-world`，runtime 命令现在必须在 `--repo-dir <repoDir>` 与 `--dev-sample-world` 之间二选一。
- 未指定 runtime target 时，CLI 会 fail-fast 报错：`缺少 runtime target，请传 --repo-dir <repoDir> 或 --dev-sample-world`。
- 同时传 `--repo-dir` 与 `--dev-sample-world` 时，CLI 会 fail-fast 报错：`--repo-dir 不能与 --dev-sample-world 同时使用`。
- `--repo-dir` 的 open-or-create sample world 语义保持不变；meta commands（`smoke` / `status` / `help`）保持不变。
- 已补最小 CLI 黑盒测试，并同步去掉 help 文案里“省略 repo-dir 就自动创建临时 sample world”的误导描述。

P5c 本轮落地结果：

- `DumpWorld()` / `DumpLocation()` 已从 `WorldSession` public surface 删除，不再把 world/location dump 伪装成 runtime 主链 use case。
- `WorldDumpRenderer` 继续留在 `TextAdv2` 内部作为 canonical formatter；宿主不直接接触 `WorldState` / `WorldDumpRenderer`。
- 显式 dev-support helper `DevTextRenderer` 已新增 `RenderWorld(WorldSession)` 与 `RenderLocation(WorldSession, string)` bridge。
- `GameServer /admin/world`、`/admin/locations/{locationId}` 与 `E2eCli --world`、`--location`、默认 world dump 路径都已统一改走同一 helper。
- `TextAdv2RuntimeCommandResult` 已无剩余主链依赖，现已删除。
- 已补 helper bridge、GameServer wiring/content-type、E2eCli `--location` 黑盒回归测试。

## 6. 不在本轮顺手做的事

- 不把 `WorldTruth` 直接升级成完整剧情/知识/可见性系统。
- 不提前引入多线程或多会话 runtime。
- 不在 canonical graph seam 定稿前大规模扩写寻路新特性。
- 不为当前草稿接口保留兼容 wrapper。

## 7. 修订后的推荐顺序

推荐顺序改为：

1. `P3c` 仅在真实 world editing 需求出现后，再处理 `Location` / `Actor` 写入权威

排序理由：

- `P4` 与 `Passage` authority seam 已基本收口，继续深挖 `WorldTruth` 叶子 setter 已经不再是当前收益最高的路径。
- `P5c` 已完成，runtime public surface 与宿主 dev-support 渲染边界已经收口。
- `Location` / `Actor` 收权在当前代码里缺少真实调用压力；过早推进容易变成为了“形式对称”而设计，而不是为真实 invariant 收口。

## 8. 每包统一验证策略

每个工作包完成时，至少执行：

- `dotnet build prototypes/TextAdv2/TextAdv2.csproj`
- `dotnet build prototypes/TextAdv2.GameServer/TextAdv2.GameServer.csproj`
- `dotnet build prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj`
- `dotnet test tests/TextAdv2.Tests/TextAdv2.Tests.csproj`

若某包只影响局部，也允许先跑受影响测试，再在主线程做一次最终串行复核。

## 9. 当前推荐起点

当前没有必须立即处理的 runtime/dev-surface 残余包；若后续出现真实 world editing 需求，再从 `P3c` 开始。

原因：

- `P5c` 已完成，runtime public surface 与宿主 dev-support 渲染边界已经收口。
- `E2eCli` 已不再把“未指定 repoDir 就开临时 sample world”放在默认主路径里，当前最显眼的宿主隐式 dev 语义已经清掉。
- `Location` / `Actor` 收权仍是触发式后续项，应等真实 world editing 需求出现后再推进。
