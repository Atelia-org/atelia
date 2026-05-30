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

## 4. 工作包顺序

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

建议切口：

- P2a：先删除 runtime command adapter（已完成，2026-05-31）。
- P2b1：先迁出已经具备 public DTO 的结构化观测。
- P2b2：再决定 actor/location/navigation/move 等结果的 public seam 形状。
- P2c：最后处理文本类 dev/admin use case 的长期归属。

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

### P3. 收紧 world root 与写入权威

主张：

- `WorldState.FromRoot` 对 `schemaVersion` 做 fail-fast。
- `WorldTruth` 写操作逐步收回到 `WorldState` / 单一 world editor。
- 叶子对象默认朝只读 façade 收口。

原因：

- 这是未来扩展世界编辑约束、删除/改名/门禁规则的前提。

建议切口：

- 先加 schema gate。
- 再逐步把 `Location` / `Passage` 的 mutator 向 `WorldState` 迁移。

完成定义：

- world root 入口具备明确版本闸门。
- 新增或修改跨实体规则时，不需要在多个叶子对象上分散维护 invariant。

### P4. 建立 canonical navigation graph seam

主张：

- 导航相关派生结构共享一份 canonical graph read model。
- `planner`、`landmark heuristic`、`graph signature`、调试观察都从这份 graph 读取。
- route acceleration 若没有真实性能压力，继续简化；必要时再保留显式缓存层。

原因：

- 当前图语义分散在多份 DTO 与派生逻辑里，是后续算法扩展的主要漂移源。

建议切口：

- 先抽出 canonical graph 投影。
- 再让 planner / heuristic / signature 改读它。
- 最后评估是否继续保留显式 `Observe/RebuildRouteAcceleration` API。

完成定义：

- 图的 enabled/filter/cost 语义只定义一次。
- route acceleration 的 stale 判定不再拼接另一份手写图描述。

### P5. 把 sample-world / dev harness 迁出 authoritative 主路径

主张：

- sample bootstrap、reset、推荐 landmarks、调试管理接口从 runtime 主路径退出。
- `GameServer` 只保留清晰的 dev-only 壳，或把相关能力显式放进 admin/dev 模块。

原因：

- 这是把“引擎本体”和“开发态演示壳”解耦的最后一步。

完成定义：

- runtime 不再承担 sample-world 生命周期语义。
- 宿主层对哪些接口是 dev-only 有清晰边界。

## 5. 不在本轮顺手做的事

- 不把 `WorldTruth` 直接升级成完整剧情/知识/可见性系统。
- 不提前引入多线程或多会话 runtime。
- 不在 canonical graph seam 定稿前大规模扩写寻路新特性。
- 不为当前草稿接口保留兼容 wrapper。

## 6. 每包统一验证策略

每个工作包完成时，至少执行：

- `dotnet build prototypes/TextAdv2/TextAdv2.csproj`
- `dotnet build prototypes/TextAdv2.GameServer/TextAdv2.GameServer.csproj`
- `dotnet build prototypes/TextAdv2.E2eCli/TextAdv2.E2eCli.csproj`
- `dotnet test tests/TextAdv2.Tests/TextAdv2.Tests.csproj`

若某包只影响局部，也允许先跑受影响测试，再在主线程做一次最终串行复核。

## 7. 当前推荐起点

推荐从 `P1. 收掉 runtime sidecar 副真相` 开始。

原因：

- 风险最高、收益最直接。
- 改动面相对集中。
- 能为后续 `P2` 的 typed runtime 收口扫清大量历史包袱。
