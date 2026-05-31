# TextAdv2 - Authoritative Boundary Refactor Plan

> 状态：进行中
> 适用范围：`prototypes/TextAdv2/`、`prototypes/TextAdv2.GameServer/`、`prototypes/TextAdv2.E2eCli/`
> 目标：把当前“概念上分层正确，但 authoritative 边界仍偏松”的 TextAdv2 收口成更稳的世界真相、算法 seam 和 runtime/host 边界

## 一句话结论

TextAdv2 当前的核心问题不是“模型拆太细”，而是三个关键边界还没有真正收口：

1. `WorldTruth` 还没有成为真正受控的唯一写入口。
2. 路径规划与 heuristic 还没有拿到一个足够小的内部 graph seam。
3. `Runtime` 仍然同时吸住 typed 业务、字符串命令总线和 sample-world bootstrap。

因此，这轮重构 SHOULD 优先做“收口”，而不是继续扩功能。

## 当前设计判断

下面这些设计判断仍然成立，不应该被这轮重构推翻：

- `Location` 只保存地点本体，不保存第二份邻接真相。
- `Passage` 作为跨地点连接的唯一真相，这条主张是对的。
- `Passage` 内部拆成 `Endpoint / Shared / Direction`，对非对称出口名、单向通行、双向异代价是有价值的。
- `ReadOnlyView` 仍然应该保留给调试、观察、外部消费，而不是被算法层反向定义。
- `AccelerationIndex` 仍然只能是可丢弃、可重建的派生层。

这轮要改的不是这些主张本身，而是它们在 API 和运行时边界上的落地方式。

## 主要问题

### 1. WorldTruth 可变面过宽

当前 `WorldState.Root`、ledger、`Location.Data`、`Passage.Data`、`Actor.Data` 以及 `Passage` 下的若干 setter，使同程序集代码可以轻易绕过 world-level 约束入口。

这会产生两个后果：

- “唯一真相”退化成“唯一存储桶”。
- 新不变量会不断外溢到 projector、planner、runtime 和测试 builder。

### 2. 算法热路径借道展示 DTO

当前导航投影、planner、landmark snapshot、route acceleration signature 之间没有共享一个更小的 graph seam。

表现为：

- `NavigationObservationProjector` 先构造较重的 `LocationObservation`，再裁成导航边。
- planner 和 heuristic snapshot 都沿着这条较重链路取数。
- tie-break 语义仍与展示字段纠缠过深。

### 3. Runtime 边界还没有分清

当前 `WorldSession` 同时承担：

- world/repo 生命周期；
- typed 业务行为；
- `enum + Arg1/Arg2` 命令总线；
- 输出渲染与 content-type 决策；
- sample-world bootstrap 入口。

这会让 dev harness 的形状过早冻结成核心 API，也会让 host 很难只依赖更清楚的运行时契约。

## 重构原则

这轮重构遵守以下原则：

- 优先收单一真源，不留兼容层。
- 优先显式 API，不依赖“同程序集内自觉不要乱改”的纪律。
- 优先抽出更小的内部 seam，而不是堆更多中间 DTO。
- 优先让 `Execute(...)` 之类外层适配器变薄，而不是继续扩张它们。
- 优先保留现有外部行为与测试语义，除非它们本身正绑错边界。

## 工作包

### 工作包 A：收紧 WorldTruth 写入口与不变量

目标：

- 收回 `WorldTruth` 中不必要的直接可变面。
- 让结构性字段创建后不再被任意重连或重命名。
- 把当前真实需要的可变操作收口成显式 world/passage 方法。
- 把 `total travel cost >= 0` 之类世界合法性约束尽量拉回 `WorldTruth`。

完成标准：

- `TestWorldBuilder` 不再依赖直接改 `Endpoint` / `Direction` 子对象来拼世界。
- 现有 world/observation 相关测试仍通过。
- 至少新增一到两个针对新不变量的测试。

非目标：

- 不在这一包里引入更大编辑器框架。
- 不在这一包里处理 runtime/host 命令面。

### 工作包 B：抽出内部导航图 seam

目标：

- 给 planner、landmark snapshot、graph signature 提供共同的最小 graph seam。
- 让 `NavigationObservationProjector` 不再依赖完整 `LocationObservation`。
- 降低 tie-break 对展示字段的依赖。

完成标准：

- planner、landmark heuristic、route acceleration signature 都改为依赖更小 graph seam。
- 现有 `ObserveNavigation` 外部输出保持稳定。
- 路由与导航相关测试仍通过。

非目标：

- 不把 `ReadOnlyView` 整体推翻成算法层。
- 不在这一包里处理 runtime typed API。

### 工作包 C：Runtime typed seam 与 host 薄化

目标：

- 让 `WorldSession` 的核心行为以更明确的 typed methods 暴露出来。
- 让 `Execute(WorldSessionCommand)` 退化为薄适配层。
- 提炼 sample-world bootstrap 入口，让 dev/bootstrap 语义更明确。
- 让 host 更直接依赖 typed seam，而不是继续扩张命令枚举耦合。

完成标准：

- `WorldSession` 至少具备覆盖现有主行为的 typed methods。
- GameServer / E2eCli 的主路径不再把 `Execute(...)` 当成唯一业务接口。
- runtime/host 相关测试仍通过。

非目标：

- 不在这一包里一次性消灭 `.textadv2-runtime-state.json`。
- 不在这一包里重做完整权限模型或多会话模型。

## 推荐顺序

推荐顺序必须是：

1. 先做工作包 A，收紧唯一真相的写边界。
2. 再做工作包 B，建立算法共同依赖的内部 graph seam。
3. 最后做工作包 C，把 runtime 和 host 边界进一步收口。

原因：

- A 会影响“什么世界状态才算合法”。
- B 应建立在更稳定的 world truth 之上。
- C 更像上层编排收口，最好吃已经稳定下来的 world 与 graph seam。

## 每包后的复核问题

### A 包完成后

- 是否仍存在明显绕过 `WorldState` 约束的主路径？
- 世界合法性约束是否还散落在 projector / planner？

### B 包完成后

- planner / heuristic / signature 是否已经共享同一图语义？
- 外部 navigation DTO 是否还只是“外部读模型”，而不再是算法真源？

### C 包完成后

- runtime 是否已经有更清楚的 typed 业务面？
- host 是否只是 host，而不是继续扩成第二套 runtime API？

## 本轮之后的自然下一步

如果这三包都收稳，下一轮最自然的入口是：

1. 评估 runtime sidecar state 是否继续存在，还是并入 repo authoritative state。
2. 评估 sample-world bootstrap 是否继续留在核心库，还是下沉到更明确的 dev support 区域。
3. 在新的 typed runtime seam 之上补更清晰的 actor-scoped / admin-scoped 接口。
