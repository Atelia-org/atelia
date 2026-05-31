# TextAdv2 重构现状与后续计划

> 状态：按 2026-06-01 当前代码与测试基线修订（含 F9 第一拍）
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
- cross-host machine contract 第一拍已完成：
  - `E2eCli` 的 session JSON-producing operations 已对齐到 camelCase
  - `E2eCli status` 也已切到 camelCase
  - 黑盒测试不再显式锁定 PascalCase
- cross-host parity 第一拍已完成：
  - 已新增独立 `CrossHostMachineContractParityTests`
  - 当前已 guard 的 canonical seam 包括：
    - observe actor
    - observe actor context
    - observe time
    - move result
    - actor route plan（`found` / `already-there`）
    - route trace json
    - minimal create location snapshot
  - 已新增 `canonical-machine-surface.md` 作为稳定 contract 说明入口
- `F9 runtime boundary freeze` 第一拍已完成：
  - durable logical time / movement trace / route acceleration 的边界已在文档与测试中正式冻结
  - sample bootstrap 已删除 `.textadv2-runtime-state.json` legacy sidecar 自动清理分支
  - sample-world-dev host 遇到 legacy sidecar-only 目录时会进入 `open-failed` / `degraded`
  - `POST /admin/reset-sample-world` 仍可恢复，但它是显式删除整个 dev repo 目录后重建 sample world 的运维动作，不应被表述成对任意脏目录都安全的自动修复

验证基线：

- `dotnet test tests/TextAdv2.Tests/TextAdv2.Tests.csproj`
- 当前结果：`132 / 132` 通过

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

结合当前代码、测试与宿主形态，当前真正还值得留在主计划里的问题已经缩到下面两类。

### 2.1 `GameServer` 的 session-unavailable contract 仍然不一致

`F9` 第一拍已经把 runtime boundary 冻住了，但它同时也让一个更具体的剩余问题变得更显眼：

- `/`
- `/healthz`
- `/admin/session-status`

这三条 operability endpoint 已经能稳定暴露：

- `host.readiness = alive`
- `session.readiness = ready | open-failed`
- `healthz = ok | degraded`

但普通 session-backed endpoint 在 session unavailable 时，当前仍主要表现为泛化 500，而不是复用这套结构化 contract。

这会带来两个直接问题：

- Agent 或测试调用者必须区分“operability endpoint 的结构化失败”和“普通 endpoint 的未收口失败”
- `sample-world-dev` 在 legacy sidecar-only 目录上的失败模式虽然已明确，但宿主整体对这种失败的外露方式仍不完全统一

因此，当前最自然的下一个主包已经不再是 runtime boundary 本身，而是把 host 的 session-unavailable contract 收口干净。

### 2.2 cross-host parity 第二批扩张仍是有价值的 tail，但已不再是主阻塞

当前第一批 canonical machine surface 已经有独立 parity guard 和单独文档说明。

仍然尚未进入第一批 guard 的 seam 包括：

- `observe-location`
- `observe-navigation`
- `observe-actor-navigation`
- `plan-route`
- `create-actor`
- `create-passage`
- `observe-route-acceleration`
- `rebuild-route-acceleration`

这些 seam 仍值得后续按需纳入 parity suite，但在当前阶段，它们更适合作为 `F10` 之后的增量 tail，而不是压过 host failure contract 的主问题。

---

## 3. 当前不该继续抬成主计划的问题

下面这些问题可以保留为文档备注或 backlog，但不应继续占据主计划中心：

- `PassageView` 是否删除
- `WorldSession` 是否改名
- `SessionService` / `HostingScaffold` 是否重命名
- `GameServer` 是否立刻补 `create-empty` / `create-sample` host endpoint
- `GameServer` / `E2eCli` 是否立刻补全全部 `SetPassage*` authoring parity
- item / inventory / flag / 更文学化命令 / 更重 gameplay

原因很简单：

- 这些问题大多已经不再制造错误 contract
- 很多只是“解释更顺”而不是“边界更对”
- 继续优先处理它们，只会把主线从 machine-first / agent-first contract 再次拉回结构整理

另外，下面这个方向也不建议继续用“顺手补一下”的方式推进：

- 直接把当前 `movement trace` / `route acceleration` 做成 durable gameplay state

原因是：

- 当前这两者在代码上都更接近 debug seam / cache，而不是 authoritative truth
- 如果真的需要 durable action history，更合理的实现会是一条新的 world-backed action log，而不是把现有 runtime 类型强行升级

---

## 4. 简化后的后续路线

后续不再推荐继续围绕 F7 展开，因为 `F7a/F7b/F7c` 已经完成。

更合理的做法是承认：

- `F1-lite` 已完成
- `F2` 已完成
- `F3` 已完成
- `F4-min` 已完成
- `F5` 已完成
- `F6` 已完成
- `F7` 已完成
- `F8a` 已完成
- `F8b` 第一拍已完成
- `F9` 第一拍已完成

因此剩余主线更适合压成一个主包加若干 tail。

## `F8: Cross-Host Machine Contract Baseline`

目标：

- 让 `GameServer` 与 `E2eCli` 在 machine-consumable 路径上真正共享同一份外部 contract

推荐拆法：

### F8a: canonical JSON contract 统一

状态：

- 已完成并验证

目标：

- 为两个宿主确立同一份 canonical JSON 约定

建议范围：

- 统一 JSON property naming policy
- 统一 enum token /状态 token 的输出约定
- 优先收口已经公开给 Agent 的 typed seam：
  - observe actor
  - observe actor context
  - observe time
  - route trace json
  - move result
  - authoring snapshot

推荐主张：

- 以当前 `GameServer` 的 camelCase contract 为 canonical baseline
- `E2eCli` 在 JSON-producing operation 上对齐到同一份 contract
- 保留 `E2eCli` 默认的人类调试输出结构，但不再保留 host-specific JSON casing

完成定义：

- 同一 typed seam 在两个宿主上不再因为字段名不同而需要双分支消费
- 测试不再分别锁一套 camelCase / PascalCase

当前已完成的实际结果：

- `E2eCli` 的 session JSON-producing operations 已统一走 camelCase
- `E2eCli status` 这个 JSON 旁路也已对齐到同一份 camelCase 约定
- enum token / status token 保持原样，没有为这次迁移引入额外兼容层
- `E2eCliBlackBoxTests` 已把显式 PascalCase 断言迁到 camelCase
- reviewer 指出的当前包内测试缺口也已补齐：
  - typed deserialization helper 不再用 case-insensitive 方式放宽 property name 断言
  - 因而 camelCase 回归现在会被黑盒测试真正抓住

当前刻意没在这一拍继续扩的部分：

- 不去统一 `GameServer` / `E2eCli` 之外的其他 JSON host
- 不抽取更大的 shared JSON factory / descriptor 基础设施
- 不把 cross-host parity 测试与文档承诺揉进同一个包

### F8b: cross-host parity 测试与文档收口

状态：

- 已完成并验证（第一拍）

目标：

- 把“两个宿主同步推进”落到可回归的 contract 断言上

建议范围：

- 为关键 machine seam 增加 cross-host parity 断言
- 在文档里明确：
  - `GameServer` 的 JSON endpoint
  - `E2eCli --json-only`
  是当前推荐的 canonical machine surface

完成定义：

- 后续再补 machine seam 时，不会再次默认允许 host-specific contract 漂移

配套文档：

- [canonical-machine-surface.md](/repos/focus/atelia/docs/TextAdv2/canonical-machine-surface.md)

当前已完成的实际结果：

- 已新增独立的 `CrossHostMachineContractParityTests`
- 当前已用递归 JSON 语义相等守住：
  - observe actor
  - observe actor context
  - observe time
  - move result
  - actor route plan（`found` / `already-there`）
  - route trace json
  - minimal create location snapshot
- `create location` 的 GameServer request body 也已显式写成原始 JSON，而不是依赖测试客户端 serializer 隐式生成
- 已新增单独的 canonical machine surface 文档，不再把稳定 contract 承诺埋在 `refactor-plan` 里

当前刻意没在这一拍继续扩的部分：

- 不把所有 JSON seam 一次性都拉进 parity suite
- 不把错误状态码 / invalid input envelope 也揉进第一拍 parity
- 不把跨宿主互操作 reopen 语义混入当前 contract 包

## `F9: Runtime Boundary Freeze`

状态：

- 第一拍已完成并验证

第一拍目标：

- 正式冻结 authoritative state、runtime debug state、runtime cache 的边界

第一拍实际范围：

- 把下面三条结论提升为显式项目决定：
  - logical time = durable authoritative world truth
  - movement trace = runtime debug seam
  - route acceleration = runtime cache
- 清理仍会混淆边界的旧兼容尾巴
  - 例如 `.textadv2-runtime-state.json` legacy sidecar 分支
- 在文档、命名、测试上明确 reopen / restart 语义是“设计结果”，不是偶然现象

当前已完成的实际结果：

- sample bootstrap 已删除 `.textadv2-runtime-state.json` legacy sidecar 自动清理 / 自动重建分支
- sidecar-only 目录现在会通过测试明确守成失败护栏，而不是静默变成 fresh sample world
- sample-world-dev host 已补集成测试：
  - legacy sidecar-only 目录启动时进入 `open-failed` / `degraded`
  - `POST /admin/reset-sample-world` 后恢复为 `ready`
- 当前结构化的 `open-failed` / `degraded` contract 只钉在：
  - `/`
  - `/healthz`
  - `/admin/session-status`
  普通 session endpoint 在 session 不可用时仍会表现为不可用主链，而不是这次顺手扩成另一套错误 envelope 包
- `canonical-machine-surface.md` 已补充 durability boundary 说明：
  - `observe-time` 既是 canonical seam，也是 durable seam
  - route trace JSON 的 shape 是 canonical，但底层数据仍是 runtime debug seam
  - route-acceleration JSON 仍非 canonical，且仍不应视为 durable contract

推荐主张（保持不变）：

- 当前不要把 `movement trace` / `route acceleration` 直接推成 durable state
- 如果未来需要 durable replay / audit / scoring，单独开一条 world-backed action log 路线

第一拍完成定义：

- 后续讨论“哪些状态应该 durable”时，不再把 trace/cache 与 world truth 混为一谈
- 代码里不再保留与旧 runtime sidecar 相关的过渡语义

## `F10: Session Unavailable Contract`

状态：

- 下一主包

目标：

- 让 `GameServer` 在 session unavailable 时，对 operability endpoint 与普通 session-backed endpoint 暴露一致、可消费的失败 contract

建议范围：

- 明确哪些 endpoint 属于 session-backed endpoint
- 在这些 endpoint 上统一捕获 session unavailable
- 统一返回 machine-friendly 的 `503` 失败响应，而不是混用泛化 500
- 尽量复用现有 `host/session` status snapshot，而不是新造一套第三份 availability model

明确不做：

- 不改动现有 ready 状态下的 JSON shape
- 不顺手扩张 `route-acceleration` 为 canonical seam
- 不把 invalid input / domain validation error 一并揉进这个包

完成定义：

- session open failure 下，调用者不再需要把 `healthz/status` 与普通 endpoint 分别按两套失败语义处理
- `sample-world-dev` legacy sidecar-only 场景与 `open-existing-only` repo 缺失场景，都能通过统一 contract 被测试守住

推荐验证：

- `GameServerIntegrationTests` 中现有 `open-failed` / `degraded` 相关用例
- 新增至少一条普通 session-backed endpoint 在 session unavailable 下返回结构化 `503` 的测试
- 全量 `TextAdv2.Tests`

---

## 5. 余下工作降为 tail backlog

下面这些工作仍然有价值，但不再建议作为“下一个主包”：

### T1: cross-host parity 第二批

内容：

- 把已经看起来稳定、又明显属于 machine-first surface 的 seam 继续补进 parity suite
- 优先：
  - `observe-location`
  - `observe-navigation`
  - `observe-actor-navigation`
  - `plan-route`
  - `create-actor`
  - `create-passage`

当前判断：

- 这是低风险、可切小包的 contract 扩张
- 但它不该先于 `F10`

### T2: host authoring parity tail

内容：

- 暴露 `SetPassage*` 到 `GameServer` / `E2eCli`

当前判断：

- 这不是设计难题，只是能力尾项
- 仅在近期世界创作真的需要时再补

### T3: durable action log / replay backlog

内容：

- durable replay
- action audit
- curriculum scoring / episode reconstruction

当前判断：

- 这确实可能成为后续 Agent Gym 的重要能力
- 但它应当建立在新的 world-backed action log 设计上，而不是复用当前 runtime trace/cache

### T4: bootstrap parity tail

内容：

- `GameServer` 是否补 `create-empty` / `create-sample`

当前判断：

- 当前已经能通过 `init-empty` / `init-sample` + `open-existing-only` 跑通真实 workflow
- 因此它不是近期主阻塞

### T5: richer gameplay backlog

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

1. `F10`：session-unavailable contract
2. `T1`：cross-host parity 第二批
3. `T2`：只在真实创作压力出现时补 host authoring parity
4. `T3`：durable action log / replay backlog
5. `T4`：只在真实生命周期压力出现时补 bootstrap parity
6. `T5`：richer gameplay backlog

这个顺序比旧计划更合理，因为：

- `F9` 第一拍已经完成，继续把它写成“下一步”只会制造文档噪音
- 当前最需要主线程继续收口的，是 `GameServer` 在 session unavailable 下的失败 contract，而不是再回头讨论 runtime boundary
- parity suite 第二批已经有清晰候选 seam，适合在 `F10` 之后连续小步推进
- durable replay 仍然重要，但它还不该抢在 host failure contract 和 parity tail 前面

---

## 7. 当前推荐的验收基线

后续每一拍都应优先复用现有测试树，而不是另起一套验证体系。

核心基线：

- `dotnet test tests/TextAdv2.Tests/TextAdv2.Tests.csproj`

重点关注的回归面：

- empty repo -> author -> observe -> move -> reopen
- 多 actor 共享世界观察刷新
- durable logical time 跨 reopen / restart 保持
- canonical machine surface 第一批 seam 在两个宿主上的 machine contract 一致
- repo 缺失 / lock failure / session open failure 下的 GameServer host readiness / status contract
- `E2eCli --json-only` 是否真的输出纯 JSON
- route trace 的文本 / JSON 双 seam 是否都稳定
- route acceleration 的 `active | stale | inactive` 与 `IsPersistent = false` 语义是否保持
- reopen / restart 后：
  - logical time 保持
  - movement trace reset
  - route acceleration reset

---

## 8. 一句话结论

TextAdv2 当前最缺的已经不是新的空间功能，而是把已经存在的能力真正收成一份稳定、统一、可长期演进的 agent-facing contract。

所以后续计划最合理的改写不是继续扩散 package，而是：

- 承认 `F7`、`F8a`、`F8b`、`F9` 第一拍都已完成
- 把下一阶段收成一个真正还没收口的主包：`F10`
- 把 parity 扩张、authoring parity、bootstrap parity、durable replay、richer gameplay 全部降成按需 tail

这比旧计划更简洁，也更贴近当前代码真实情况。 
