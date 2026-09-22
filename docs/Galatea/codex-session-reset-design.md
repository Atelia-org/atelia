# Galatea 日常解除 Codex 绑定与新会话

> 状态：已完成三位独立审阅者的两轮质疑与交叉质询，尚未实施；2026-09-23。用户已选择停服后的 operator 命令。
> 本文扩展 [专用 Codex Home 设计](codex-home-isolation-design.md)，本轮不操作真实 delegation-state 或 Codex session。

## 1. 最小操作模型

增加一个离线命令 `operator reset-codex-binding`：解除所选 Character 的 Codex route 绑定，下一封具备派发资格的排队邮件沿用现有 `Unbound -> Binding -> Bound` 流程创建新 thread。此处 session 指 Codex thread，不是 Galatea 的 SessionJournal session。

普通操作仅解绑；如果有活动邮件，默认拒绝。额度耗尽、旧 Home 不再使用等情况下，操作者可显式指定该活动邮件的 dispatch ID，结束本地等待、生成结果不明回信并解绑。它不取消远端执行，不删除 Codex 历史，不自动重发旧任务。

软件不检测 Provider/model/toolset 是否变化，不增加 session generation、pending-reset 标记、Home 指纹或线程迁移器。何时需要新会话由操作者决定，当前绑定仍由原来的 `route_binding` 唯一表示。

## 2. 需求与证据账本

| 来源 | 结论 |
|:--|:--|
| 用户当前需求 | 更换 Home 或 Provider 后，需要日常可用的解绑/后续邮件新建会话机制，不能依赖旧 session 失败后耗尽恢复预算 |
| 用户本轮选择 | 停服后的 operator 命令；不要求运行时网页按钮、延迟切换请求或热更新 |
| [GalateaDurableDelegationDriver](../../prototypes/Galatea/GalateaDurableDelegationDriver.cs) | 每 Character 的 FIFO 先处理 active mail；无 active 才选择 queued；`Unbound` 可走已有创建绑定路径，无队列不创建 thread |
| [route/mail 形状](../../prototypes/Galatea/GalateaDelegationState.cs)、[binding transitions](../../prototypes/Galatea/GalateaDelegationSqliteStore.Transitions.cs) | route 有 State、BindingOperationId、ThreadId、ActiveDispatchId、QuarantineCode、Revision。Queued 不预绑定 thread，Started 才冻结派发身份和 task commitment |
| [恢复事务](../../prototypes/Galatea/GalateaDelegationSqliteStore.Recovery.cs) | 已有 `FinishMailLocally` 把不能证明未派发的活动邮件终结为 `RESULT_UNCONFIRMED`，同一事务生成 Ready notice、清绑定；不需要伪造八次恢复失败 |
| [store 生命周期](../../prototypes/Galatea/GalateaDelegationSqliteStore.cs) | readonly/writable strict open 均持有既有每 Character lifetime lock，且会校验 owner、格式、限额和快照不变量 |
| [operator recovery](../../prototypes/Galatea/GalateaDelegationOperatorRecovery.cs)、[Program](../../prototypes/Galatea/Program.cs) | 现有离线入口在 web host/provider/sidecar 创建前分流；已有 dry-run / apply 习惯。当前没有日常 reset-binding 命令 |

持久化代码与测试是设计依据；没有读取或修改用户提到的真实 `prototypes/Galatea/.atelia/galatea/delegation-state`。更换 Provider 后不必先证明旧线程已损坏，显式请求新上下文本身就是操作理由。

## 3. 命令与用户可见行为

以下是拟新增命令形状，当前不可直接执行：

```bash
# 预览：默认不写入，不启动 Codex
Galatea.Server operator reset-codex-binding \
  --config /absolute/path/to/config.json --character alice

# 无 active mail 时：解绑，保留队列和既有回信
Galatea.Server operator reset-codex-binding \
  --config /absolute/path/to/config.json --character alice --apply

# 有 active mail 且决定结束本地等待时：精确指定预览中的 dispatch ID
Galatea.Server operator reset-codex-binding \
  --config /absolute/path/to/config.json --character alice \
  --abandon-active <dispatch-id> --apply
```

`--character` 精确匹配配置中的 `CharacterId`，不是显示名或任意目录。一次只处理一个 Character；实例共享 Home 的其他 Character 如需换新会话，分别执行。不增加跨多个 SQLite 的全有或全无批处理协议。

预览和 apply 输出只含受限元数据：Character、route 状态/修订、旧 thread ID、active dispatch/state、queued 数量、待消费回信数量、拟进行的变化和结果。无任务正文、凭据或 provider 原文。CLI 可把自选配置路径作为操作上下文，但 Warning/Error 日志遵守 content-free 约束。

若有 active，未给 `--abandon-active` 返回 `ActiveMailRequiresDecision`，说明其状态和 exact ID，不修改数据。给错 ID 拒绝，不把参数解释成“放弃随便哪封当前邮件”。持久 `Started` 在冷重开后不能证明尚未发送，按不确定工作处理。

`--abandon-active` 表示“放弃自动等待/采集旧任务结果”，不表示任务没有执行、执行失败或撤销外部修改。已知完成证据需要正常结算或现有 completed-recovery 流程；本命令不猜测完成结果。

## 4. 状态决策表与最小事务

先 strict-open 并取得现有 lifetime lock。锁被服务持有就拒绝，dry-run 也不旁路锁。操作前应已停止 Galatea 及所属 sidecar/app-server；数据库锁不能证明外部进程已退出。

| route / mail 状态 | 普通 reset | 带 exact `--abandon-active` |
|:--|:--|:--|
| Unbound，无 active | `AlreadyUnbound`，零写入 | 仅在 exact 旧 mail 已 `RESULT_UNCONFIRMED` 且 notice 一致、route 仍 Unbound 时返回 `AlreadySatisfied`；其他情况拒绝 |
| Bound，无 active | 清 route 绑定，`Unbound` | 拒绝：指定的 active 不存在；不要借旧参数清掉后来新建的绑定 |
| Binding，无 active | 清 BindingOperationId，`Unbound`；queued 不变 | 拒绝：没有对应 active；崩溃可能留下外部空线程，但不能因此推断存在已派发任务 |
| Bound + Started / OutcomeUnknown / Accepted | 拒绝并显示 active | exact ID 一致才允许：旧 mail 终结、回信、解绑在同一事务完成 |
| route/mail Quarantined 或快照不合法 | 拒绝，走既有诊断/恢复 | 同样拒绝；日常操作不绕过隔离或身份冲突 |

### 4.1 无 active 的解绑

增加 store 内一个窄操作：校验 expected route revision，确认无 active 且状态为 Bound/Binding，在一个 `ExecuteWrite` 中复用 `ReleaseRecoveryRoute(resetBinding: true)` 清 `BindingOperationId` / `ThreadId` / `ActiveDispatchId` / `QuarantineCode`、设 `State=Unbound`，并调用 `IncrementStoreRevision`。不复制第二份 route 更新 SQL。提交后显式 `ReadSnapshot` 严格验证快照，不能假设每次 `ExecuteWrite` 正常返回都已自动完成完整读回。

不改 capture、baseline/frontier、任何 mail 的状态/正文/顺序/失败预算、Ready/Leased/Consumed notice 或现有 reply lease。已有回信未消费不妨碍解绑；它属于旧邮件结果，不依赖未来发信使用哪条 thread。

### 4.2 显式结束活动邮件并解绑

operator 在持有同一 store lifetime lock 时，先按决策表对当前快照作窄分类，再传当前 mail/route revision 调用 `FinishMailLocally(..., resetBinding: true)`，保留其默认 `MayHaveDispatched`。该 helper 允许 Quarantined 且会在已有 terminal 时提前返回旧 notice，不能把它本身当作命令资格检查，也不能为了 operator 改掉它现有较宽的恢复语义。

复用的终结事务把 exact mail 置为 TerminalFailed、terminal code 为 `RESULT_UNCONFIRMED`，生成原 dispatch 唯一的失败 notice，清 active 和 route 绑定。沿用现有 semantic notice 及旧 thread/turn 身份保存规则；不将 `Started` 伪装成受控 NotDispatched，不改成 Queued，不制造新 dispatch 来重发原任务。

这条操作不增加恢复失败计数，也不把计数补成上限；终结依据是 operator 的明确决定，回信继续使用已有“结果未确认”事实。无需新增 terminal 状态或错误码来声称远端取消成功。

合法 active mail 已预留一个终结回信槽和相应字节；strict snapshot 校验实际 Ready/Leased 占用加 active reservation 不超过上限。本次沿用空 body/detail 的已有失败 notice，可兑换该预留，无须先恢复服务消费旧回信。保留原容量校验和事务回滚；损坏状态或不再容纳预留的限额在 strict-open 阶段拒绝，不以删回信或清 lease 作为补救。

新失败 notice 使用下一 completion sequence，保持既有 Ready/Leased notice 和冻结 reply lease 不变，不把新 notice 塞进已经冻结的 lease。

### 4.3 冷重开、重跑和操作失败

- dry-run 不写库；apply 重新取得锁并按当前状态重新判定，不盲信上一次预览。
- 解绑与可选终结均是一个 store 事务；不允许已终结无回信，或旧 active 尚在但 route 已清的半状态。
- 提交后输出失败/进程退出时，在恢复服务前重新 strict-read 或重跑；route 仍 Unbound 时普通解绑返回 AlreadyUnbound。exact 放弃操作依据既有 terminal mail + notice + Unbound 返回 AlreadySatisfied，只证明当前目标状态满足，不证明历史终结来自本命令；没有新请求 ID 或审计表。
- 普通 reset 每次操作执行时的当前绑定；如果期间服务已经建立新绑定，再次普通 `--apply` 是一次新的解绑，应重新预览。其零写入幂等只覆盖持续 Unbound 的情况，不增加 CLI expected revision 参数；store 内部仍使用现有 revision CAS。
- 如果 route 已被正式服务重绑或已有另一封 active，带旧 dispatch 参数的重跑拒绝；这项精确保护不适用于没有旧对象参数的普通 reset。
- 本地终结以后，晚到的旧结果不自动改写该 terminal，不作为成功回信插回；继续遵循首个持久终结生效的规则。

## 5. 下一封邮件的准确含义

reset 影响“接下来尚未派发的 FIFO 头”，包括执行命令之前已排队的邮件；不只影响命令之后新捕获的邮件。保留排队顺序、原 dispatch ID 和重试预算，沿用既有派发、拒绝和终结规则。reset 不修复队头投影失败、背压等问题，不保证重启后立即前进：例如当前 `LocalProjectionRejected` 不终结原邮件，不能承诺自动跳过它。

解除绑定不立即调用 Codex。重启后有可派发 queued mail，driver 才按当前 `codexHome` / Provider / model 创建新 thread、设置 ownership、发送该邮件。其后邮件复用这条新 thread，直到下次 reset 或原有失效恢复。

新 session 不带旧 Codex 对话上下文，但工作目录和文件照常保留。邮件正文仍是原来提取并持久化的内容，不自动拼接历史或重写“继续刚才”的邮件。操作者在预览中看到 queued 数量；确实需要上下文的任务应提供自足说明。是否增加取消某封 queued mail 的能力是另一需求，本设计不通过丢弃队列解决。

## 6. 配合 Home / Provider 切换

1. 停服，保留原有 delegation-state；按需要先处理已知结果。
2. 对需要新会话的 Character 预览 reset。无 active 直接 apply；有 active 则选择保留等待，或用 exact dispatch ID 显式结束本地等待并 apply。
3. 准备新的 Home 或修改固定 Home 中的 Provider 配置，核对有效 SQLite 位置和认证。更换 Home 时，可先解绑再改配置；不必让旧 Home 保持可恢复才能执行解绑，因为命令只访问 Galatea store。
4. 用隔离任务验证新 Provider；结束探针后恢复服务。下一封可派发邮件创建新 thread；如果 Provider 不可用，继续现有有限恢复，不把 reset 当成可用性保证。

这为首次采用专用 Home 提供了不迁移旧 Codex 会话的正常路径：保留 Galatea 自己的 capture、队列、回信、lease 和历史终结，仅解除未来发信绑定。旧 Codex 数据保留在原处供查阅；不删除整个 delegation-state，也不复制个人 Home 作为解绑前提。

## 7. 实现范围与验收

新增窄 operator handler，在 Program 的现有通用 `operator` 分支之前按 exact command 分流；复用配置加载、Character 选择、store lock、strict open 和已有终结事务。新增无 active 的 store reset 操作；不扩展 `local-codex-mcp` wire，不改 Provider 协议，不新增数据库列/表或 SessionJournal 格式。

| 验收 | 必须观察的行为 |
|:--|:--|
| 空闲 Bound / Binding | reset 后 Unbound；队列、旧回信与 lease、capture 完整不变；无 Codex RPC；重复零写入 |
| 有活动邮件 | 默认拒绝；wrong dispatch 拒绝；Started/Unknown/Accepted exact abandon 后恰一失败 notice + route 解绑，原任务不重发 |
| active 容量预留 | 实际回信加 active reservation 恰好满额时仍成功兑换失败 notice；不清旧回信/lease；非法容量快照 strict-open 拒绝 |
| 锁与异常状态 | 服务持锁拒绝；Quarantined/身份冲突拒绝；不创建缺失 store |
| 提交/输出失败 | 冷重开无半状态；持续Unbound的普通重复零写入；exact重复不重复回信、旧dispatch参数不能解除新绑定 |
| 队列与新 thread | FIFO 原邮件发送到新 thread；之后复用新 thread；不把前一 mail 的历史/正文自动重放 |
| 结果投递 | reset 前已有 Ready/Leased notice 与冻结lease不变；新增notice不加入旧冻结lease，后续按现有观察/lease 流程仅消费一次 |
| 更换 Home | 临时 Home A 建旧绑定，offline reset，再从 Home B 启动；下一封新任务绑定 B 中新 thread，Galatea store 没有重建 |

## 8. 辩证简化裁决

三位审阅者以 §2 同一账本独立核对代码后，交换容量、helper 宽语义和陈旧命令的最强失败轨迹；主线程复核源码，第二轮收敛，无需第三轮。用户对入口的决定始终保持为离线 operator，未扩展成网页控制或待执行 reset 状态。

| 裁决 | 项目与依据 |
|:--|:--|
| keep | exact `--abandon-active`：预览 A 后若已出现 B，布尔 force 会放弃错误任务；现有 dispatch 身份足够，不需另一套请求身份 |
| keep | operator 窄分类 + 既有终结事务：防止宽 helper 的 quarantine 接纳和 terminal 提前返回越过命令合同 |
| merge | 空闲 reset 的 route 清理复用 `ReleaseRecoveryRoute`；不复制绑定更新 SQL |
| simplify | 去掉“先消费回信才能 abandon”的正常分支；活动任务现有预留已保证终结容量 |
| simplify | AlreadySatisfied 仅描述当前结果；普通 reset 仅对持续 Unbound 幂等，不许诺跨重绑的陈旧命令保护 |
| delete | reset 自动让所有坏队头结算并让位的承诺；当前 projection rejection 会保留原队头 |
| defer | 在线按钮、pending reset、批量原子切换、取消 queued、自动补历史与操作来源审计；当前单角色离线动作无此消费者 |

语义守护者在明确“普通 reset 作用于执行时当前绑定”后撤回新增 CLI expected revision 的未决项；其他审阅者据 active reservation 和投影代码撤回初稿的额外运维/队列推进假设。相对初稿，减少一个正常阻塞分支和两个过强承诺，仍为一个新命令、一个窄 store 操作、零新增数据库字段。真实实例操作不属于本次文档工作。
