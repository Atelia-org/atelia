# TextAdv2 重构现状与后续计划

> 状态：按 2026-06-01 当前代码与测试基线修订
> 适用范围：`prototypes/TextAdv2/`、`prototypes/TextAdv2.GameServer/`、`prototypes/TextAdv2.E2eCli/`
> 背景：当前 TextAdv2 仍处于草稿期，无旧数据、无兼容包袱，应优先追求单一真源、清晰 contract、可验证的小步推进

---

## 0. 这份文档现在负责什么

这份文档现在不再承担“继续大范围设计诊断”的职责。

它的目标收敛为三件事：

1. 记录当前已经真实落地、并被测试验证的边界。
2. 明确剩余真正阻塞 `RL Agent Gym` 主线的少数问题。
3. 把后续计划压缩成更小、更顺手、更容易连续实施的工作流。

---

## 1. 当前实际状态

### 1.1 已完成并验证的主线能力

截至当前工作树，下面这些能力已经落实并被 `TextAdv2.Tests` 验证：

- `WorldTruth` authoritative seam 已稳定：
  - `MoveActor` 直接基于 `WorldTruth` receipt 产出结果
  - entity id / root integrity gate / durable logical time 都已进入 authoritative state
- `ReadOnlyView -> Session -> Host` 主分层已成立：
  - 纯读 DTO 稳定返回 `ReadOnlyView` 结构
  - session-owned movement trace / route acceleration 已从 `ReadOnlyView` 迁出
- durable logical time 已完成：
  - `currentLogicalTick` 已持久化在 world root
  - reopen / host restart 后时间保持一致
- session-level authoring seam 已完成：
  - `CreateLocation`
  - `CreateActor`
  - `CreatePassage`
  - 全套 `SetPassage*`
- bootstrap 第一拍已够用：
  - `WorldSession.CreateEmpty(repoDir)`
  - `E2eCli init-empty <repoDir>`
  - `E2eCli init-sample <repoDir>`
  - `GameServer` 显式 `BootstrapMode = sample-world-dev | open-existing-only`
- host-level 最小 authoring 已完成：
  - `GameServer` 已有 `POST /admin/locations` / `actors` / `passages`
  - `E2eCli` 已有 `--create-location` / `--create-actor` / `--create-passage`
- 多 actor 共享世界基线已钉住：
  - authored mini-world 下的共址观察、他人移动可见、move receipt shared presence 都已补测试
- agent context 第一拍已完成：
  - 新增 `ActorContextObservation`
  - `WorldSession.ObserveActorContext(actorId)`
  - `GameServer GET /actors/{actorId}/context`
  - `E2eCli --observe-actor-context <actorId>`
  - 测试已覆盖 shared presence、durable time、disabled exit 不混入 `AvailableMoves`

验证基线：

- `dotnet test tests/TextAdv2.Tests/TextAdv2.Tests.csproj`
- 当前结果：`113 / 113` 通过

### 1.2 当前原型已经具备什么

用一句话概括：

TextAdv2 现在已经是一个“可创作最小空间、可驱动多 actor、可持久化空间与逻辑时间、可向 Agent 输出第一版结构化 context”的原型。

更具体地说：

- 本体已经足够支撑“空间沙盒”：
  - 世界可以从空 repo 启动
  - location / actor / passage 可以稳定创建
  - 观察、移动、路线规划、逻辑时间都已可回归
- 两个宿主都已经能跑通最小 workflow：
  - create/open world
  - author minimal topology
  - observe / move / plan / verify
- 当前最主要的结构不确定性已经明显减少：
  - `PassageView`、`WorldSession` 改名、wrapper 命名这类问题，已经不是主路径阻塞

---

## 2. 复勘后的真实阻塞

结合当前代码、测试与宿主形态，后续真正还值得留在主计划里的问题，已经缩到下面三类。

### 2.1 `GameServer` 还没有可靠的 readiness / failure contract

当前 `GameServer` 已经能报告 host-level status，但还不能稳定表达“进程活着”和“session 已可用”的区别。

当前实际表现：

- `/healthz` 总是返回 `ok + host-running`
- `/admin/session-status` 也仍然偏 host-level 描述
- 在 `open-existing-only + repo 不存在` 的情况下，`/admin/world` 会失败，但 `/healthz` 仍报告正常

这对外部脚本、Agent、PipeMux 或长驻 orchestration 来说，是当前最明确的 operability 缺口。

### 2.2 `E2eCli` 还不是稳定的 machine-first host

当前 `E2eCli` 已经有不少 JSON 输出能力，但仍保留明显的“人类调试器”输出习惯：

- 默认总会打印 `TextAdv2 session repo: ...`
- 多操作调用还会插入 `[1/2]` 这类标题
- JSON 和 banner / heading 混合输出
- 现有黑盒测试仍需要手动抽 JSON block

这意味着它适合作为 e2e 容器，但还不适合作为“稳定机读入口”。

### 2.3 route trace 仍然只有文本 seam

目前：

- `GameServer /actors/{actorId}/route-trace` 返回纯文本
- `E2eCli --trace-actor-route` 也返回纯文本

这本身不是 bug，但它使“调试移动历史”和“给 Agent / 脚本消费移动历史”仍然耦合在一起。

在 F6 已经落下 `ActorContextObservation` 之后，下一条最自然的 machine seam 候选就是 route trace。

---

## 3. 当前不该继续抬成主计划的问题

下面这些问题可以保留为文档备注或 backlog，但不应继续占据主计划中心：

- `PassageView` 是否删除
- `WorldSession` 是否改名
- `SessionService` / `HostingScaffold` 是否重命名
- `GameServer` 是否立刻补 `create-empty` / `create-sample` host endpoint
- `GameServer` / `E2eCli` 是否立刻补全全部 `SetPassage*` authoring parity
- movement trace / route acceleration 是否立刻持久化
- item / inventory / flag / 更文学化命令 / 更重 gameplay

原因很简单：

- 这些问题大多已经不再制造错误 contract
- 很多只是“解释更顺”而不是“边界更对”
- 继续优先处理它们，只会把主线从 machine-first host 再次拉回结构整理

---

## 4. 简化后的后续路线

后续不再推荐把工作拆成很多平行 package。

更合理的做法是承认：

- `F1-lite` 已完成
- `F2` 已完成
- `F3` 已完成
- `F4-min` 已完成
- `F5` 已完成
- `F6` 已完成

因此剩余主线其实几乎都属于同一个方向：

## `F7: Host Operability Baseline`

为了让这一包真正可行，推荐把它拆成三个顺序子步，而不是继续保持一个过大的总包。

### F7a: `GameServer` readiness / failure contract

目标：

- 让外部调用方能区分：
  - host alive
  - session ready
  - session degraded / open failed

建议范围：

- 收口 `/healthz`
- 收口 `/admin/session-status`
- 明确 repo open failure / session unavailable 时的可机读状态
- 保持 host-level JSON contract 简洁，不引入复杂兼容层

完成定义：

- repo 缺失、repo 锁冲突、session open 失败时，host status 不再继续伪装成完全 ready
- 对应集成测试能稳定断言 ready / degraded / failed 语义

### F7b: `E2eCli` machine-output mode

目标：

- 让 `E2eCli` 至少有一种明确模式可以输出纯机读结果，而不是混入 banner / heading

建议范围：

- 新增一个明确命名的纯机读模式
  - 例如 `--json-only`、`--machine`，最终命名后定
- 只要求 JSON 类命令支持该模式
- 保留当前默认调试输出，不强行破坏人工体验

完成定义：

- 单条 JSON 命令可以稳定返回纯 JSON
- 黑盒测试不再需要先从 banner 文本里截 JSON block

### F7c: route trace machine seam

目标：

- 给 route trace 增加结构化消费入口，而不是只剩文本 renderer

建议范围：

- 保留现有文本 trace 作为调试输出
- 另外增加结构化 route trace seam
- 两个宿主都暴露同一批核心字段

完成定义：

- `GameServer` 与 `E2eCli` 都可返回可反序列化的 trace 结构
- 测试明确锁住 reopen 后 trace reset 的现有语义

---

## 5. 余下工作一律降为 tail backlog

下面这些工作仍然有价值，但不再建议作为“下一个主包”：

### T1: host authoring parity tail

内容：

- 暴露 `SetPassage*` 到 `GameServer` / `E2eCli`

当前判断：

- 这不是设计难题，只是能力尾项
- 仅在 F7 或近期世界创作真的需要时再补

### T2: bootstrap parity tail

内容：

- `GameServer` 是否补 `create-empty` / `create-sample`

当前判断：

- 当前已经能通过 `init-empty` / `init-sample` + `open-existing-only` 跑通真实 workflow
- 因此它不是近期主阻塞

### T3: runtime-owned state decision gate

内容：

- movement trace 长期定位
- route acceleration 长期定位

当前判断：

- 这仍然重要
- 但它更适合作为 F7 之后的设计结论包，而不是在 host operability 之前抢占主线

### T4: richer gameplay backlog

内容：

- item / inventory
- world flag / 开关 / passage condition
- 更文学化命令
- 更重叙事层

当前判断：

- 明确延后
- 只有在 Agent Gym 主线真的需要更丰富互动状态时再回看

---

## 6. 修订后的推荐顺序

按当前实际情况，推荐顺序改为：

1. `F7a`：`GameServer` readiness / failure contract
2. `F7b`：`E2eCli` machine-output mode
3. `F7c`：route trace machine seam
4. `T1`：只在真实创作压力出现时补 host authoring parity
5. `T3`：runtime-owned state decision gate
6. `T2`：只在真实生命周期压力出现时补 bootstrap parity
7. `T4`：richer gameplay backlog

这个顺序比旧计划更合理，因为：

- 它直接对准当前最真实的宿主 contract 缺口
- 它把一个过大的 `F7` 包拆成了三个连续的小包，更适合连续实施
- 它承认 `F1/F4` 剩余项已经不是设计主问题，只是尾项
- 它避免在 host 还不 machine-first 时过早讨论更重玩法

---

## 7. 当前推荐的验收基线

后续每一拍都应优先复用现有测试树，而不是另起一套验证体系。

核心基线：

- `dotnet test tests/TextAdv2.Tests/TextAdv2.Tests.csproj`

重点关注的回归面：

- empty repo -> author -> observe -> move -> reopen
- 多 actor 共享世界观察刷新
- durable logical time 跨 reopen / restart 保持
- actor context 在两个宿主上的主结构一致
- repo 缺失 / lock failure / session open failure 下的 host readiness / status contract
- CLI 机读模式是否真的输出纯 JSON
- route trace 的文本 / JSON 双 seam 是否都稳定

---

## 8. 一句话结论

TextAdv2 当前最缺的已经不是新的结构重构，而是把宿主真正收成“能被脚本、PipeMux 和 Agent 稳定消费”的 machine-first 原型。

所以后续计划最合理的改写不是继续扩散 package，而是：

- 承认 F1-F6 主线已经基本完成
- 把剩余主线压缩成 `F7a -> F7b -> F7c`
- 把 authoring parity / bootstrap parity / richer gameplay 全部降成按需 tail backlog

这比旧计划更简洁，也更贴近当前代码真实情况。 
