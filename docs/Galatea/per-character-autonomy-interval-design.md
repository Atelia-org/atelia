# Character 级自主激活间隔设计

状态：**设计已收敛，尚未实施**。本文定义用每个 Character 的自主激活间隔替代
`heartbeatEnabled` 的 V11 硬切方案。它不是当前运行合同；在实现、测试和单一开发实例的人工配置切换完成前，
现有 V10 `config.json` 仍按 [V10 合同](../SessionJournal/current/contracts/galatea-root-config-v10.md)工作。

## 结论

最小模型是一个 Character 级字段：

```json
{
  "v": 11,
  "characters": [{
    "id": "example",
    "autonomyIntervalMinutes": 30
  }]
}
```

`autonomyIntervalMinutes` 是唯一的空闲自主激活 authority：

- `0`：关闭**无 Ready 回信时**的周期 `HeartbeatActivation`；负数拒绝。
- 正整数：该 Character 在一次成功主线回合后，以该分钟数重新计时；10 秒检查只带来最多约一个 tick 的发现延迟，不改变配置的间隔含义。
- 推荐第一版的合法正值为 `1..525_600`（一年）；这是输入与诊断投影的有限安全界，不是配额政策。若实际需要更长周期，应以真实用例调整此界，而不是引入第二种关闭哨兵。

选择 `0` 而不是 `-1`：关闭没有第二种业务状态；接受两种哨兵只会扩大严格 parser、迁移和诊断的组合数。

`0` **不**关闭 durable delegation/mail 的推进，也不让已持久化的 Codex Ready reply 饿死。它只禁止没有 Ready reply 时由时间触发的新 `HeartbeatActivation`。这正是本次“状态推进”和“自主活动”分开的落点。

## 先纠正术语：当前有三种独立 cadence

| 名称 | 当前入口 | 周期/触发 | 是否受 `heartbeatEnabled` 控制 | 是否会直接创建主模型回合 |
|:--|:--|:--|:--|:--|
| durable delegation fallback pulse | [`GalateaDelegationSupervisor`](../../prototypes/Galatea/GalateaDelegationSupervisor.cs) | 1 秒 fallback 加合并 signal | 否 | 否；推进 SQLite outbox、binding、inspect/recovery |
| character-mail relay sweep | [`GalateaCharacterMailRelay`](../../prototypes/Galatea/GalateaCharacterMailRelay.cs) | 1 秒 sweep 加 signal | 否 | 仅按独立 internal-mail 合同接纳入箱回合 |
| automatic coordination pulse | [`GalateaServerAgentHostedService`](../../prototypes/Galatea/GalateaServerAgentHostedService.cs) | 每 Character 10 秒 | **是** | Ready reply 优先；空时才检查 10 分钟自主激活 |

因此，提问中的判断有一半正确、也需要更精确：`heartbeatEnabled` 不会停掉前两种持久状态推进；但它目前确实把第三种 10 秒协调循环、Ready reply 自动续接和固定 10 分钟空激活绑在一起。代码证据是 coordinator 在 Ready cutoff 为空后才调用
`AutonomyCadence.ObservePulse()`，而 hosted service 只枚举 `HeartbeatCharacterIds`。

## 保留的产品律

| 来源 | 必须保留的可观察行为 |
|:--|:--|
| 用户本次决定 | 每个 Character 有自己的自主激活间隔；关闭只针对自主激活；不同模型配额不能被全局 10 分钟常量限制。 |
| 当前 cadence / 测试 | monotonic process-local 计时、重启重新 arm 且不补跑、成功的任一主线回合 reset、autonomous 非完成回合 pause。 |
| 当前 admission / durable lease | `TurnLock → reconcile → exact Idle → connection → PrepareFreshTurnAdmissionAsync → BeginCutoff`；Ready reply 优先；busy 跳过；未知外部结果不盲重试。 |
| 当前 durable scheduler | delegation 和 character-mail 的 1 秒 liveness 不受 interval 值影响。 |
| 当前持久输入 | 角色实际配置的 interval 是 heartbeat Observation 的语义事实，不能对 1/60 分钟角色仍保存或展示“十分钟”。 |

本方案的解释性决定是：已 durable 的 Ready reply 是外部工作结果，不是“空闲自主激活”。即使
`autonomyIntervalMinutes: 0`，它仍可触发一次严格受 recovery/lease 约束的 `DelegateReply`；没有 Ready 或需恢复 lease 时，绝不因为 tick 创建主线回合或 provision session。

## V11 root config：硬切，不双读

V11 的 Character 对象删除 `heartbeatEnabled`，新增**必填** JSON integer
`autonomyIntervalMinutes`。`0` 是明确的 default-off，不把缺字段悄悄解释成关闭。

```json
{
  "v": 11,
  "characters": [
    { "id": "low-cost", "autonomyIntervalMinutes": 10 },
    { "id": "premium",  "autonomyIntervalMinutes": 60 },
    { "id": "manual",   "autonomyIntervalMinutes": 0 }
  ]
}
```

实施时同步改动：

1. `GalateaStrictConfigReader.CurrentConfigVersion`、精确 token、Character whitelist 和错误文本改为 V11；`heartbeatEnabled` 作为 unknown field 拒绝。
2. file DTO / resolved `GalateaCharacterConfig` 改为已验证的分钟数或 `TimeSpan? AutonomousActivationInterval`；删除 `HeartbeatEnabled`、`HeartbeatCharacterIds` 和 `ReadHeartbeatCharacterIds`，不保留优先级规则或兼容 property。
3. bootstrap template、root-field tests、配置指南、运行指南和 V11 current contract 使用同一字段语言。
4. 删除已无输入消费者的 V9→V10 converter/upgrade command 及其测试，不把它改造成 V10→V11 通用迁移器。历史合同和验收记录仍作为历史材料保留。

本仓只有一份 V10 开发实例，因此切换是一次人工、停服的配置维护，而不是产品化迁移功能：先在实例状态目录之外备份 config，手动将 `v` 改为 `11`，并把每个 `heartbeatEnabled:true` 替换为 `autonomyIntervalMinutes:10`、`false` 替换为 `0`，再启动新 binary。启动前的新 strict parser 必须拒绝旧 V10；startup 不读、写或转换 V10。这次人工编辑不改 SessionJournal、delegation SQLite、CharacterMemory 或历史 Observation。

V11 而不是重定义 `v:10` 的原因不是下游兼容层，而是诚实的 schema 身份：当前 V10 已把 bool 类型和默认值写入 strict contract。新 binary 硬拒 V10；不需要 runtime dual reader、converter、plan/apply/resume 或自动实例迁移。

## 调度与恢复：一个 safe spine，两个 policy

不要为每个 interval 建 timer，也不要复制 admission。保留 10 秒的低成本检查和既有 coordinator 的安全 spine，把 policy 分开：

```text
每 Character 10 秒 tick
  interval > 0
    → attach / 原 reply-first admission
    → Ready: DelegateReply
    → Empty: 用该角色 interval 检查并可能 HeartbeatActivation

  interval == 0
    → pure durable wake probe
    → None: 返回（不 attach、不 provision、不调用 provider）
    → ReadyNotice 或 ActiveReplyLease: attach / reply-only admission
    → Ready: DelegateReply
    → Empty 或 recovery blocked: 返回（绝不进入 cadence）
```

### 为什么必须有 pure wake probe

`GetSessionAsync` 可能为 `create-if-missing` 创建 repository，不能在每个零间隔角色的每个 tick 调用。现有
`GalateaDelegationSupervisor.ReadMailboxStatus` 已承诺纯读：不 attach、不初始化、不 signal、不推进状态。
V11 应在相同 deferred SQLite read 中增加仅内部使用的闭合 projection，例如：

```text
AutomaticWakeReason = None | ReadyNotice | ActiveReplyLease
```

- `ReadyNotice` 来自 Ready notice 的存在；预读只是唤醒提示，绝不是 lease claim。attach 后仍以 `BeginCutoff` 的事务结果为唯一领取事实。
- `ActiveReplyLease` 是必要的恢复唤醒。反例：零间隔角色已 claim reply lease 后进程崩溃；notice 已非 Ready，若只看 Ready，冷启动永不 attach/reconcile，也就把 `inProgress` / recovery 边界藏起来。该 projection 只表达存在性，不泄露 lease、turn、正文或 identity。
- unavailable、uninitialized、maintenance、quarantine/backoff/read failure 都 fail closed，不从“非 Ready”推断空 mailbox，更不 attach。

预读和最终 cutoff 之间的竞争是正常的：另一入口若先领取，reply-only path 在 `BeginCutoff` 得到 `Empty` 或 busy，创建零 live turn；它不能退化成 HeartbeatActivation。

保持现有 per-Character hosted tasks 和 shutdown drain 语义即可；是否将它们合并为一个 host-wide timer不属于本变更。立即 signal 优化也不属于本变更：10 秒 durable-store fallback 已提供 liveness，未来 signal 只能是合并提示，不能成为 Ready 事实或替代 fallback。

### Cadence 的最小代码变化

`GalateaAutonomyCadence` 继续是 `TurnLock` owner 的 process-local monotonic state machine；将静态
`IdleInterval = 10 minutes` 改成构造时注入的 immutable positive `TimeSpan`。零间隔不创建/arm cadence。

不新增 deadline 表、wall-clock scheduler 或 SQLite migration。这样保留：重启重新计时、不补停机任务、claim/rollback 的精确补偿、成功回合 reset 和 autonomous failure pause。config 修改仍要求重启，新的 host 持有新的 immutable snapshot。

`POST /mailbox/ready-turn` 和 retry-admission 也必须改为 reply-capable policy：零间隔下它们可在 wake evidence 存在时 attach/reconcile，但 retry 仍只结算旧 admission，不能借 endpoint 创建无 Ready 的新回合。

Agent status 不再把 interval=0 叫作 `disabled`；最小 public contract 是复用 `waiting` 且
`nextActivationAtUnixTimeMilliseconds=null` 表示“没有自主 deadline、仍监视 durable reply”。配置/运行指南必须写明这层含义和 default connection。若 UI 需要更强可见性，再以单独 API 版本增加 `reply-waiting`，不要借配置字段重复表达。

## Observation wire：interval 是持久事实

目前 `galatea.observation.v1` 严格要求
`action.externalIntervalMinutes == 10`，并且 canonical Markdown、Memo recall query 和测试都使用该值。这是第二个隐藏的全局十分钟常量。

V11 必须同时引入一个新的、闭合的 structured Observation schema（建议
`galatea.observation.v2`）：

| 记录 | reader | writer / renderer |
|:--|:--|:--|
| 已存 v1 heartbeat | 继续严格接受 `externalIntervalMinutes: 10` | 保持原有“十分钟”投影；不重写历史 |
| 新 v2 heartbeat | 严格接受 `1..525_600` 的 snapshot | `HeartbeatActivation`、JSON action、Markdown 和 recall query 都使用本轮 snapshot |
| 非 heartbeat / 旧 legacy Markdown | 保持既有 schema 和 parser | 不借本改造改变它们 |

因此 `GalateaFreshInput.HeartbeatActivation`、`PlayerTurnObservation` 和其 read/projection 都要持有
interval snapshot，而不是在读取历史时从当前 config 重算。v2 的 narrative 可渲染为“又有 {n} 分钟流逝”，但仍表示已接受的周期性机会，**不是**测量到的精确 wall-clock downtime。

不要放宽 v1 到任意整数：那会让同一 schema ID 在旧/新 parser 中代表不同的 strict language，并使历史 contract 不可辨。也不要把单位改为 seconds；当前 durable field、用户场景和 10 秒调度精度均以分钟更自然。未来真实的 sub-minute 产品需求才授权另一次 schema/调度演进。

## 辩证审查裁决

三名独立只读审阅者完成两轮质询与一轮只针对 Ready/recovery 的裁决。下表记录收敛后的最小模型，而不是按票数取胜。

| 项目 | 裁决 | 删除或保留的原因 |
|:--|:--|:--|
| `heartbeatEnabled` + interval 双字段 | **delete** | 会形成 `false + positive`、`true + 0` 两个无 canonical authority 的状态。 |
| `0` 和 `-1` 双哨兵 | **delete -1** | 没有不同产品语义；负数统一 fail closed。 |
| per-Character timer / durable deadline | **defer** | 10 秒 probe 已足够；deadline 持久化会错误引入 restart catch-up 和迁移面。 |
| delegation supervisor / mail relay cadence | **keep** | 各自拥有 durable liveness，关闭自主活动不应停掉 outbox、reconciliation 或 internal mail。 |
| 现有 admission spine | **keep** | 删除会丢失 TurnLock、lease FIFO、exact Idle、recovery 与 claim rollback。 |
| interval=0 的 attached-only 资格 | **delete** | attach 是 process-local 偶然状态；重启后不可预测，也不是 provider/work authorization。 |
| `automaticReplyEnabled` 新字段 | **delete** | 用户已把关闭限定为无 Ready 的周期自主激活；durable Ready / active lease 是可验证的独立唤醒事实。 |
| v1 arbitrary interval 放宽 | **delete** | interval 是持久语义，需 v2 writer 与 v1 exact reader，而非 silent grammar mutation。 |
| V10 runtime compatibility reader 或 V10→V11 converter | **delete** | 单一开发实例手动停服备份后切换足够；双读、plan/apply/resume 与自动迁移均无消费者。 |
| V9→V10 upgrade implementation | **delete** | 当前只有 V10 实例，保留历史文档即可；不让旧 converter 继续制造无法被 V11 binary 读取的文件。 |
| immediate supervisor→coordinator callback | **defer** | 是延迟优化，不是正确性要求；10 秒 fallback 仍是唯一 liveness 基线。 |

概念数从一个含混 `heartbeatEnabled` 的“总开关”收敛为：一个自主 interval、一个 pure wake reason、一个既有 safe admission spine；并明确保留两个原本独立的 durable scheduler。没有引入通用 scheduler、持久 deadline 或第二个配置开关。

## 最小垂直实施切片

1. **V11 config cut**：DTO、strict reader、resolved model、template 和 root-language tests；删除 V9→V10 converter/CLI 与其 tests。为唯一开发实例写停服/备份/人工字段替换的操作说明；代码测试只验证新 parser 拒绝 V10，不实现或测试转换器。
2. **Cadence parameterization**：positive minutes injection、零 interval 不 arm、custom interval due/reset/no-catch-up/failure-pause tests；保留原 10 秒 tick。
3. **reply-only wake**：internal wake projection（Ready + active lease）、zero interval no-store/no-mail zero-call proof、reply-only coordinator mode、race/restart/uncertain recovery tests；更新 ready-turn/retry/status semantics。
4. **Observation v2**：snapshot carrying input, v1/v2 exact read matrix, renderer/recall propagation, legacy v1 reopen proof；不得重写 SessionJournal history。
5. **operator/docs**：V11 current contract、configuration/runtime/server API/README、browser strict status consumer（若必要）和 doc scope；最后只在已停服、备份后的唯一开发实例上人工切换 config 并验证。

## 验收矩阵

| 场景 | 必须证明 |
|:--|:--|
| strict config | V11 接受 `0`、`1`、`10`、`60` 和上界；拒绝负数、小数、overflow、旧 bool、缺字段和 unknown。 |
| resource isolation | interval=0 且 store missing/uninitialized/unavailable 或无 wake 时，反复 tick 不 attach、不 provision、不调用 normalizer/extractor/main provider。 |
| configured cadence | 多 Character 各按自己的分钟 interval due；10 秒仅影响发现延迟；成功 main turn reset；autonomous failure pause；restart 不 catch up。 |
| Ready priority | positive interval 时 Ready 先于 due activation；zero interval 的 Ready 只产生 `DelegateReply`，永不产生 heartbeat turn。 |
| race / recovery | preflight 到 cutoff 被其他入口抢占时 zero live turn；active lease 冷重启仍 attach/reconcile；`RESULT_UNCONFIRMED`、recovery required、reply/admission failure 均无 blind replay。 |
| durable independence | 任意 interval 组合下 delegation 1 秒 fallback 和 character-mail 1 秒 relay 仍推进其自身状态。 |
| Observation | v1 `10` reopen 不变；v2 保存本轮分钟 snapshot，Markdown 和 recall 一致；非法 v2 interval 拒绝。 |
| lifecycle / API | maintenance/shutdown 不因 scan attach；ready-turn/retry 的 zero policy 明确；status 的 null next deadline 不再误称“完全 disabled”。 |

建议实现后按仓库既有串行方式运行 Galatea focused/full Debug 与 Release tests、Release build、Node HTTP/SSE checks、文档检查和 `git diff --check`；本设计阶段没有运行这些实现验证，也没有调用真实 provider。
