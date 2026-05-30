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
- `TextAdv2RuntimeCommandResult` 的残余面，已主要收缩到：
  - `MoveActor`
  - `PlanRoute` / `PlanActorRoute`
  - `TraceActorRoute`
  - `MoveActorQuiet`
  - `DumpWorld` / `DumpLocation`
- `P3` 在代码里并没有形成一个可整体落地的包：
  - `schemaVersion` 字段已经写入 world root；
  - `WorldState.FromRoot(...)` 已具备 schema gate；
  - `Location` / `Passage` 的写入权威仍明显分散在叶子对象上。
- `P4` 也不是“从零开始”：
  - `NavigationObservationProjector.ObserveLocationNavigationGraph(...)` 已经被 planner、landmark heuristic、route-acceleration stale 判定复用；
  - 它实质上已经是一个隐式 canonical graph seam，只是还没正式收口。
- `P5` 需要改写得更具体：
  - sample-world 创建、默认 landmark profile、open-or-create/reset 等 dev 语义虽然已有 helper，但仍在 runtime 主路径中留下明显耦合。

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

状态：进行中

主张：

- `TextAdv2Runtime` 不再返回 `string + contentType`。
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
- P2b2c：只保留 `MoveActor` 这一条核心写入 use case 的 typed seam。
- P2c：处理 text/dev/admin surface 的长期归属，包括 `MoveActorQuiet`、`TraceActorRoute`，以及是否继续保留 runtime 直接输出文本。

完成定义：

- `TextAdv2Runtime` 的 public surface 不再暴露宿主渲染协议。
- CLI/HTTP 仍通过现有用例工作，但渲染责任在宿主项目。

P2a 本轮落地结果：

- `TextAdv2RuntimeCommand` / `Execute(...)` 已从 runtime 主对象删除。
- `RuntimeScaffold` 的状态说明已同步到“runtime 直接暴露 typed methods，宿主自己做调用分发”。
- 下一切口是 `P2b1`，不是继续扩大 `P2a`。

P2b1 本轮落地结果：

- `ObserveTime()` / `AdvanceTime(long)` 已直接返回 `TextAdv2LogicalTimeObservation`，由 `GameServer` / `E2eCli` 在宿主边界序列化。
- `ObserveRouteAcceleration()` / `RebuildRouteAcceleration(...)` 已直接返回 `TextAdv2RouteAccelerationObservation`，对应 HTTP/CLI 入口也已迁到宿主边界序列化。
- 对应的 runtime 测试，以及 time/route-acceleration 的 GameServer JSON 边界断言，已跟随迁移完成更新。
- 下一自然入口是 `P2b2`：为 actor/location/navigation/move 设计更干净的 public DTO seam，再继续缩减 `TextAdv2RuntimeCommandResult` 的残留范围。

P2b2a 本轮落地结果：

- `ObserveLocation(string)` 已直接返回 `TextAdv2RuntimeLocationObservation`。
- `ObserveActor(string)` 已直接返回 `TextAdv2RuntimeActorObservation`。
- 新 seam 没有直接公开 `ReadOnlyView` DTO，也没有把 `TravelMode` 公开成 public enum；runtime 在边界前把 travel mode 投影成字符串 token。
- `GameServer` / `E2eCli` 对应入口已迁到宿主边界序列化，相关 runtime / GameServer 测试已更新到 typed 断言。
- 下一自然入口是 `P2b2b`：决定 navigation observation 是否沿用同类 runtime-facing DTO 策略，或与 `P4 canonical navigation graph seam` 一起收口。

P2b2b 本轮落地结果：

- `ObserveNavigation(string)` 已直接返回 `TextAdv2RuntimeLocationNavigationObservation`。
- `ObserveActorNavigation(string)` 已直接返回 `TextAdv2RuntimeActorNavigationObservation`。
- 新 seam 没有直接公开 `ReadOnlyView.NavigationObservation*`，也没有把 `TravelMode` 公开成 public enum；runtime 继续在边界前把 travel mode 投影成字符串 token。
- `GameServer` / `E2eCli` 对应 navigation 入口已迁到宿主边界序列化，相关 runtime / GameServer 测试已更新到 typed 断言。
- 下一自然入口是 `P2b2c`：决定 `MoveActor` 是否也采用独立 runtime-facing DTO seam，或先转入 `P4 canonical navigation graph seam` 做内部图真源收口。

P2 后续修订说明：

- `MoveActor` 仍应留在 `P2`，因为它是最后一个明显属于 runtime 核心 use case、但仍停留在 `string/contentType` seam 上的写入入口。
- `PlanRoute` / `PlanActorRoute` 不再视为 `P2b2c` 的自然尾巴；它们更适合放到 `P4` 之后，等 canonical graph seam 收口后再决定 public typed DTO。
- `DumpWorld` / `DumpLocation`、`TraceActorRoute`、`MoveActorQuiet` 不再视为“typed runtime seam 收尾”，而是显式归类到 `P2c` / `P5` 的 text-dev-admin surface。

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

### P4. 建立 canonical navigation graph seam

状态：建议优先级上调

主张：

- 导航相关派生结构共享一份 canonical graph read model。
- `planner`、`landmark heuristic`、`graph signature`、调试观察都从这份 graph 读取。
- route acceleration 若没有真实性能压力，继续简化；必要时再保留显式缓存层。

原因：

- 当前图语义分散在多份 DTO 与派生逻辑里，是后续算法扩展的主要漂移源。
- 但它并不是“从零开始”的新想法；当前已经存在一个被多处复用、但尚未正式命名的 graph seam。

修订后的建议切口：

- P4a：把现有 `ObserveLocationNavigationGraph(...)` 及其等价语义正式收口成 canonical graph seam。
- P4b：让 planner / landmark heuristic / route-acceleration stale signature 显式改读这份 seam，而不是继续把它当作 projector 内部细节。
- P4c：在 graph seam 稳定后，再决定 `PlanRoute` / `PlanActorRoute` 的 public typed seam，以及是否保留显式 `Observe/RebuildRouteAcceleration` API。

完成定义：

- 图的 enabled/filter/cost 语义只定义一次。
- route acceleration 的 stale 判定不再拼接另一份手写图描述。

### P5. sample-world / dev harness 下沉

状态：建议改写并前移

主张：

- sample-world 创建、默认 landmark profile、open-or-create/reset、debug dump 这类开发态语义从 runtime 主对象退出。
- `GameServer` / `E2eCli` 只保留显式的 dev/admin 壳，不再让这些策略隐式穿过 runtime 主路径。

原因：

- 当前最大的真实问题不是“有没有 helper”，而是 runtime 仍直接依赖 `TestWorldBuilder` 和 sample-world policy。
- 这件事与 `MoveActor` seam、`P4` 都相对正交，适合拆成更具体、可单独验收的小包。

修订后的建议切口：

- P5a：把 sample-world profile / 默认 landmark policy 从 `TextAdv2Runtime` 中剥离到显式 dev support 层。
- P5b：把 open-or-create/reset 等 dev bootstrap 语义从 runtime 主路径继续下沉，并收清 `GameServer` admin surface 的边界。
- P5c：处理 `DumpWorld` / `DumpLocation` 等稳定调试文本输出的最终归属。

完成定义：

- runtime 不再直接依赖 `TestWorldBuilder`、默认 landmark profile、或 open-or-create/reset 语义。
- 宿主层对哪些接口是 dev-only 有清晰边界。

## 6. 不在本轮顺手做的事

- 不把 `WorldTruth` 直接升级成完整剧情/知识/可见性系统。
- 不提前引入多线程或多会话 runtime。
- 不在 canonical graph seam 定稿前大规模扩写寻路新特性。
- 不为当前草稿接口保留兼容 wrapper。

## 7. 修订后的推荐顺序

推荐顺序改为：

1. `P5a sample-world profile / 默认 landmark policy 下沉`
2. `P2b2c MoveActor typed seam`
3. `P4 canonical navigation graph seam`
4. `P4c / P4-adjacent route plan typed seam`
5. `P3b world editor / 写入权威收口`
6. `P2c + P5b/P5c` 清理 text/dev/admin surface

排序理由：

- `P5a` 当前已经反向渗入 runtime 主对象，继续拖延会让后续每一包都带着 sample-world policy 包袱。
- `MoveActor` 是剩余 runtime 核心 use case 中最自然的一条 typed seam。
- `P4` 的真实价值高于 route plan public seam；应先收内部 graph 真源，再决定 route plan 的对外契约。
- `P3b` 目前还缺承接设计，放在更后面更可行。
- text/dev/admin surface 最后一起清，会比一边做核心 seam 一边分散清理更稳。

## 8. 每包统一验证策略

每个工作包完成时，至少执行：

- `dotnet build prototypes/TextAdv2/TextAdv2.csproj`
- `dotnet build prototypes/TextAdv2.GameServer/TextAdv2.GameServer.csproj`
- `dotnet build prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj`
- `dotnet test tests/TextAdv2.Tests/TextAdv2.Tests.csproj`

若某包只影响局部，也允许先跑受影响测试，再在主线程做一次最终串行复核。

## 9. 当前推荐起点

当前推荐从 `P5a sample-world profile / 默认 landmark policy 下沉` 开始。

原因：

- sample-world policy 目前仍直接渗进 `TextAdv2Runtime`，已经开始反向污染后续各包的边界。
- 它与 `MoveActor` seam、`P4` 相对正交，适合在继续扩张 runtime public surface 前先下沉。
- 做完它之后，再推进 `MoveActor` seam 或 canonical graph seam，都会更干净。
