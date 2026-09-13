# Galatea per-user home 设计与实施

> 状态：已实施并完成本机升级与验证；保留原 Codex thread，删除执行配置的 policy hash 绑定。
> 日期：2026-09-13。具体版本与验证记录见文末实施记录。

## term `User-Home` 用户个人文件目录

每个 Galatea user 拥有长期保留的个人目录，Codex 代行者默认在其中操作相对路径文件。初始部署为 `/galatea-homes/cyber`、`/galatea-homes/gpt`，与项目代码目录完全分开。已有用户保留原 Codex thread；下一封新任务使用当前 homeDir，旧任务仍按原身份取回结果。

### decision [S-HOME-PRODUCT-BOUNDARY] 用户决定与范围

- 用户指定初始根目录为 Linux 顶级目录 `/galatea-homes/`。
- 只需要通常各自编辑各自文件，不要求强隔离、防恶意篡改或审计。
- 延续原 Codex thread；CWD 是可变执行配置，不是 thread、用户或历史邮件的永久身份。
- 保留正常业务正确性：任务不重复派发、结果不串到其他任务、已知回信不丢失。
- 用户已授权具体设计、代码实施、本地落地及按需 Git 提交。
- home 不自动更改 Unix `$HOME`、`~`、`CODEX_HOME`、Codex 登录身份。SessionJournal、CharacterMemory 和 delegation SQLite 继续使用各自状态目录。

## 需求账本与证据

| ID | 要求或事实 | 来源 / 当前消费者 |
|:--|:--|:--|
| U1 | 各 user 有个人文件目录；初始根为 `/galatea-homes/` | 用户两轮请求；`cyber`、`gpt` |
| U2 | 无强隔离、防恶意篡改、审计需求 | 用户最新明确说明，高于旧方案假设 |
| U3 | 保留原 thread，允许配置重置 CWD | 用户最新纠正；原固定 thread 的连续上下文 |
| C1 | 独立 user store/driver/thread，共享 transport | [Supervisor](../../prototypes/Galatea/GalateaDelegationSupervisor.cs)；不同 user 并发、同 user active-first |
| C2 | Codex resume/turn 请求已有 cwd 覆盖字段 | [ThreadResumeParams](../../local-codex-mcp/schemas/v2/ThreadResumeParams.ts)、[TurnStartParams](../../local-codex-mcp/schemas/v2/TurnStartParams.ts) |
| C3 | 旧 bridge 在 resume 前拒绝旧 CWD 不同，也在 inspect 中检查旧目录 | [backend](../../local-codex-mcp/src/codex/backend.ts) 的 startBoundTurn/readOwnedThread/readInspectionThread；限制已从 Galatea 路径移除 |
| C4 | 旧 policy hash 覆盖 CWD、mode、工具和限额，绑定 owner/route/历史邮件 | [durable contract](../../prototypes/Galatea/GalateaDelegationDurableContract.cs)、[Snapshot](../../prototypes/Galatea/GalateaDelegationSqliteStore.Snapshot.cs)；配置一致性门禁已删除 |
| C5 | 实际结果关联使用 dispatch/thread/turn/task，不使用 policy hash | [driver](../../prototypes/Galatea/GalateaDurableDelegationDriver.cs)、backend 的 inspectAcceptedTurn/inspectUnknownDispatch |
| C6 | 五项持久限额还被单独比较并用于容量校验 | [RequireOwner](../../prototypes/Galatea/GalateaDelegationSqliteStore.cs)；本次不开放任意限额变更 |

上表的 C3/C4 记录重构前的限制；它们是问题证据，不是必须保留的需求。实施验证区分源码、隔离测试和 pinned Codex 实测。

## 配置与目录

### spec [F-HOME-CONFIG] 一个业务目录字段

root config V9 增加必需的 `users[].homeDir`；delegates V3 删除全局 `routes[0].cwd`。同步 reader、模型、模板和合同，不保留旧字段 fallback。

示意片段，不是完整配置：

```json
{
  "users": [
    { "userId": "cyber", "homeDir": "/galatea-homes/cyber" },
    { "userId": "gpt", "homeDir": "/galatea-homes/gpt" }
  ]
}
```

初始 `allowedRoots` 为 `["/galatea-homes"]`，只用于校验新执行目录。已有 thread 的旧 metadata CWD 可以在该范围之外；读取它的历史结果不等于访问那个目录。

不增加 homesRoot、路径 override、workspace ID 或目录 registry。运行中 driver 捕获当前 user home 和共享执行策略即可，不需要“有效路由指纹”概念。

### spec [S-HOME-DIRECTORY] 目录校验与环境

- 部署时创建根目录及两个子目录，确保运行身份可写；启动只校验，不自动修补权限。
- homeDir 为现存 Linux absolute canonical directory；C# 配置加载复用现有 canonical/no-symlink 规则。不同 home 不可相同或互相包含，也不与内部状态目录相同或嵌套。
- TS 对新的执行目录复用现有 `PathPolicy` 的 realpath 和 allowedRoots 检查。通用 MCP 路径语义不变；不新增跨语言路径框架。
- sidecar/app-server 固定以 `/` 为进程 CWD；可执行文件和 entryPoint 都为绝对路径。业务 CWD 始终逐请求提供，进程目录不是 fallback。
- 实施时核对 pinned Codex 的实际进程 CWD、配置与指令来源；固定 `/` 不意味着不加载全局配置，也不意味着强隔离。

## 可变执行配置与任务身份

### spec [S-HOME-NO-POLICY-IDENTITY] 删除 route policy hash 绑定

删除 `CreateRoutePolicyFingerprint`、driver 的 `RequirePolicy` 及以下三处字段和相关校验：

- delegation owner/meta 的 `route_policy_fingerprint`；
- route binding 的 `policy_fingerprint`；
- outbound mail 的 `frozen_route_policy_fingerprint`。

不把它缩成另一个配置 hash，不建立历史 policy registry，不再要求历史终态匹配当前执行配置。CWD、mode、工具开关均按后续新任务的当前配置使用。旧字段没有独立业务消费者；保留它们只会让正常配置修改导致全库拒读。

保留有实际用途的关联：store 的 userId/sessionRepositoryId、source action 与 artifact ordinal、dispatchId、threadId、accepted turnId、task 匹配、回复及 lease 状态。它们分别防止开错用户库、重复提取/派发、误认任务结果、重复消费回信。

本次不是全仓库删除 hash。用于任务去重、内容一致性或冻结请求恢复的其他 digest，只有在找到替代实际消费者的更小机制后才调整；不把“无审计需求”扩大为允许重复副作用或串回信。五项持久限额及降限时已有内容如何处理也另属容量语义，本次保持不变。

### spec [S-HOME-REQUEST-PATH] 同一 thread 使用当前目录

保留一个共享 sidecar/app-server。内部协议增加两处必需目录参数，查询结果不带 CWD：

```text
ensure-binding(bindingOperationId, cwd)
start-turn(dispatchId, threadId, task, cwd)
inspect-dispatch(dispatchId, threadId, task, expectedTurnId)
```

同步更新 C# [transport](../../prototypes/Galatea/GalateaDurableDelegateTransport.cs)、[wire client](../../prototypes/Galatea/GalateaCodexDurableSidecarClient.cs)、TS [protocol](../../local-codex-mcp/src/galatea/durable-protocol.ts) 与 [adapter](../../local-codex-mcp/src/galatea/durable-adapter.ts)，使用精确 wire V4。创建/派发缺失或非法目录必须拒绝，不能误用通用 PathPolicy 的默认目录。

Galatea [sidecar config](../../local-codex-mcp/src/galatea/sidecar-config.ts) 和 [bootstrap](../../local-codex-mcp/src/galatea-durable-sidecar.ts) 移除固定业务 cwd、`CODEX_BRIDGE_DEFAULT_CWD` 要求及单 CWD ready 日志；bootstrap 使用 `PathPolicy.create(allowedRoots)`。普通 MCP 的默认目录接口不改。

新任务的执行顺序：

1. 校验目标 home，确认正确的 owned thread 和不存在 active turn。
2. 删除旧 CWD 必须存在、属于当前 allowedRoots、等于新 home 的 Galatea 前置要求。尤其不能保留 `readOwnedThread` 中更早的目录阻断；普通 MCP 消费者仍维持其原有边界。
3. `thread/resume(threadId, cwd: homeDir)` 延续原会话；随后 `turn/start` 同样显式传 homeDir 与对应 `writableRoots: [homeDir]`。
4. 区分 response 顶层有效 `cwd` 与 `response.thread.cwd` 历史 metadata。后者不能继续被当作当前执行目录或 thread 身份。

[官方 App Server 文档](https://learn.chatgpt.com/docs/app-server)说明 turn 级 cwd 覆盖会成为后续 turn 默认值。本地另一份 Codex 源码还有“冷 resume 顶层 cwd 与历史 thread.cwd 不同”及“热 loaded thread 重入时忽略部分 resume override”的分支/测试；它不是当前 pinned 版本的运行证明。因此实施验收覆盖冷/热 resume 和实际新 turn 目录。热 resume 返回旧有效 cwd 时，若显式 turn override 正常生效，不应仅因此阻断；若 pinned 实测不能采纳目标目录，再选择最窄重载措施，不预建协调器。

### spec [R-HOME-OLD-RESULTS] 旧结果按任务身份恢复

`inspectDispatch` 只读查询原 thread/dispatch/task/accepted turn；不 resume、不 start，不对旧 `thread.cwd` 做存在性、allowedRoots 或等于当前 home 的校验。目录不存在也不妨碍读取已存结果。

保留现有 active-first：Started/OutcomeUnknown/Accepted 先沿原任务查询和结算，绝不因改目录重新发送；完成后下一封尚未派发的 Queued 邮件使用当前配置。正常 Ready/Leased 回信、提取状态与主线 frozen request 继续原恢复流程。

不再要求为 CWD 修改排空队列、消费全部回信或清空所有冻结请求。已有 Quarantined 仍按原有处置处理，目录修改不自动解除真正的身份/结果冲突，也不应再制造 CWD-only quarantine。

### spec [S-HOME-PROMPT] 角色与代行者知道个人目录

Galatea 专用静态 [backend profile](../../local-codex-mcp/src/galatea/backend-profile.ts) 说明当前 CWD 是发信 user 的个人空间；无需动态 profile 工厂。角色的 [prompt composer](../../prototypes/Galatea/GalateaSystemPromptComposer.cs) 在 outbound-mail 能力说明中加入配置派生的实际 homeDir。

新鲜请求使用新说明；已冻结 Prepared request 不重新拼装，以保持原请求恢复语义。旧 Codex 历史仍可提及原路径，文件本身不会因 resume 自动移动。

## 首次格式升级与部署

### spec [R-HOME-ONE-TIME-UPGRADE] 结构升级一次，常规改目录无需专门迁移

首次实现将 delegation SQLite V1 升至 V2，删除 policy 三字段及其校验。停止相关写入者、取得已有独占锁、备份后运行窄格式升级；保留 user/session owner、原 thread binding、邮件状态、任务正文、回信、lease 和去重事实。复用 SQLite 事务完成本地格式变换及重开验证，不为被删除的 hash 建新的证明文件或历史映射。

离线入口为 `Galatea.Server operator upgrade-delegation-store --config <absolute-path> --user <userId>`；默认只检查，加 `--apply` 才执行。使用更新后的 V9/V3 配置。命令在原 store 锁内创建相邻 V1 SQLite 备份，再事务删除两列并重建带新版本 CHECK 的 meta 表；普通启动和只读打开不隐式迁移。升级到 V2 后重复执行返回 `AlreadyCurrent`。命令不构造 web host、transport 或 provider。

同步修改 Supervisor、store/schema/snapshot/driver 与现有 [OperatorRecovery](../../prototypes/Galatea/GalateaDelegationOperatorRecovery.cs) 的 owner 构造，避免离线恢复仍依赖已删除 policy 字段。该命令不扩展为 CWD 迁移器。

升级不调用 Codex、不重发任务；保留 active/unknown 等业务状态交给原恢复路径。无需对 Galatea DB 和 Codex 状态建立跨库事务：Galatea 不再持久绑定当前执行 CWD，Codex 在之后正常 resume/start 时接收它。

之后常规 homeDir 修改只需更新配置并重启。先结算旧任务，再在同一 thread 使用新目录。初次部署准备 `/galatea-homes/`，按实际清单搬移需要的个人文件，不复制整个项目树，不改 Codex 私有 SQLite/rollout。

## 验收与文档归属

### spec [A-HOME-VERTICAL-SLICE] 最小验收

1. 两个 user 在同一共享 app-server 使用不同 home，并发操作同名相对路径，实际文件互不覆盖；重启后保持原 threadId。
2. 一个已有历史 thread 从旧目录切换到 home；冷/热 resume 加新 turn 实际使用目标目录，不误用旧 metadata CWD。旧目录移出 allowedRoots 后仍可续接。
3. 外部已接受、Galatea 尚不确定时改目录重启：按原 dispatch 取结果，零重复 start；下一封 Queued 才使用新 home。旧目录已不存在时，历史结果仍可查询。
4. 用实际旧格式夹具覆盖 terminal/Ready/Leased/active/unknown/queued 等代表状态；升级保持业务内容和身份，仅删除无消费者 policy 字段。事务中断可重试或回到升级前，不遗留半升级库。
5. 错 user/thread/turn/task 仍拒绝；错误目录在新执行前拒绝；主线 frozen canonical request bytes 不变，新 fresh request 包含 home 说明。
6. maintenance 不启动外部调用，共享 transport 仅释放一次；现有 OperatorRecovery 可打开升级后的对应用户库。
7. 核对实际子进程 CWD、配置和指令发现。provider-free fixture 证明参数与恢复，显式 live canary 证明真实文件操作；两种证据分开报告。

测试用隔离临时目录，不要求 CI 创建系统根目录；不新增测试平台。[配置参考](configuration.md)、root config 合同、[runtime](runtime.md) 和受影响的 delegation 合同随代码同步更新。

### derived [A-HOME-REVIEW-REVISION] 辩证审查的修正

前一轮三方审查保留了已有 fingerprint 并提出历史保全/切换门槛。用户明确无审计需求后，主线程重新查实际消费者，Minimal architect 与 Semantic defender 独立复核并交叉质询，撤回“历史 frozen policy 必须保留”和“旧 thread 连续性待选择”的结论。现状约束不能反过来制造用户需求。

| 裁决 | 对象 | 当前理由 |
|:--|:--|:--|
| delete | policy hash、三个持久字段、driver policy equality | 不参与任务关联；使可变配置成为全库永久身份 |
| delete | inspect 的 CWD 参数及旧目录校验 | 读取结果不访问工作目录；保留反而阻断真实旧结果 |
| simplify | 原 thread resume/start | 校验新执行目录，允许旧 metadata 不同；无需 rebind/CWD 迁移器 |
| delete | Q1、新 thread 分支、全量排空门槛 | 用户已明确方向；状态机可以先恢复旧任务再执行新任务 |
| keep | user/session/dispatch/thread/turn/task 关联、active-first、reply lease | 防止开错库、重复副作用、串结果及重复消费 |
| defer | 通用限额修改、其他 digest 清理 | 需各自业务消费者证据；不扩大本次 home 功能 |

最终机制为一个共享 transport、一个 per-user homeDir、两类执行请求目录参数、零结果查询目录参数。需要一次本地 store 格式升级，此后 CWD 修改不再要求历史策略迁移。

## 实施记录（2026-09-13）

按配置/prompt、TS 协议/backend、SQLite/driver/离线升级三个工作包实施，独立审阅后由主线程完成
C# transport/Supervisor 接线和集成验证。当前版本为 root V9、delegates V4、wire V4、delegation SQLite V2。delegates V4 的原生配置继承规则见 [configuration](configuration.md)。

已通过的隔离验证：Galatea Server 全集 882 passed、1 个默认跳过的 live test；代行/恢复/升级与
TextExtractor 定向 243/243；Node 全集 106 passed、2 个默认跳过的 live tests；文档 scoped 检查
29 files、0 diagnostics。迁移夹具覆盖十类状态、
事务提交前后故障重试、备份、重复运行、业务列保留和 active/unknown 零重复 start。

本地配置已设置 `/galatea-homes/cyber`、`/galatea-homes/gpt`，目录为空；未识别出需搬移的个人文件，
原文件保留原处。更新前确认无 live writer，并以现有备份脚本完成全量归档及完整性校验；随后两个 user
分别 dry-run、apply 并严格重开。两库均为 V2、`quick_check=ok`；gpt 原 thread、38 captures、
9 TerminalCompleted + 1 TerminalFailed、10 Consumed replies 保留，cyber 仍为 Unbound 空库。
状态库旁另有升级命令生成的 V1 SQLite 备份。本轮没有启动常驻 Galatea 服务。
本机全量备份为 `/mnt/e/bak/atelia-galatea-20260913-020423-SGT.7z`。

pinned `0.154.0-alpha.3` 的 provider-free 探针实际确认 Node 和 native Codex 进程 CWD 均为 `/`，
新 ephemeral thread 有效 CWD 为临时 home；全局 `/root/.codex/config.toml` 仍参与配置。
本次 `instructionSources` 返回空数组，仅记录观察结果，不据此宣称全局指令不会加载。

C# V4 真实 transport canary 已通过 1/1：隔离目录、新建测试 thread，验证实际 start/Accepted、
重复 dispatch 拒绝与匹配 token 的 Completed。它不向现有 Galatea 用户投递任务。

TS V4 实际文件 canary 已通过 1/1（约 40 秒）：两个 user 在共享 app-server 并发写同名相对文件，
内容分别落在自己的 home；原 thread 冷重启与热 resume 后均写入新 home；旧目录删除且移出
allowedRoots 后，Accepted 和 OutcomeUnknown 两种查询都能得到原结果。官方分页最终核对两个
测试 thread 恰有 3/1 个 turn，无额外 start。sidecar、Node wrapper 和 native Codex 的实际进程
CWD 均为 `/`。该结果证明隔离测试 thread 的真实目录切换，不把它描述为向现有用户发过测试邮件。

实际接线发现并修正两个已有的过严前提：新空 thread 尚无 source rollout 时不能要求分页历史成功；
`turn/start` 可返回 `itemsView=notLoaded` 且 `items=[]`，不能因此拒绝已关联 RPC 返回的 turnId。
缺少消息投影时不制造 live 结果证据，之后仍按 accepted turnId 和真实持久 items 核对 dispatch/task。
这两处修正不引入自动重发，也不把历史查询失败解释为任务不存在。
