# Galatea 可重复实验与验收

## 目标与第一期范围

把“开发期代码正确”和“有状态实例能走完操作流程”接成可执行验收，同时保留真实 journal/SQLite 持久化。
实验使用独立合成实例，不在长期 `gpt`/`cyber` 会话中试错。不修改 SessionJournal 的 frozen identity、
uncertain recovery、tool-loop 或外部 exactly-once 合同。

第一期采用三个有界工作包（explorer → worker → independent review → 集成验证）：

1. `GalateaScenarioLab`：完整合成状态目录、明确外部依赖、stop/reopen、成功清理/失败保留。
2. `GalateaUpgradeRehearsalTests`：旧投影 fingerprint 的 Prepared/Started 被新版拒绝 → 真 CLI 显式回退 →
   新回合经过 production Codex converter/受控 HTTP/parser 完成 → 冷重开验证。
3. `GalateaProcessCrashRehearsalTests`：正式 Server 子进程向本地 provider 发出请求后硬杀 → 重启拒绝隐式重试 →
   exact head 显式授权后完成。

补充 live canary 只用 `gpt-5.6-luna` 合成数学输入，经过 Galatea HTTP/主线与真实 Codex client，
两轮之间释放并重新打开同一实例，验证结果、native reasoning 和 Idle 持久状态。

## 一键运行

从任意目录运行（需要仓库现有 .NET SDK、Python 3；首次运行可能 restore 已声明的依赖）：

```bash
/repos/focus/atelia/scripts/test_galatea_lab.sh
/repos/focus/atelia/scripts/test_galatea_lab.sh Debug
```

默认只运行 `GalateaLab` 场景和 lab lifecycle 测试，不访问真实 provider；没有匹配/执行测试也不能算成功。
`.trx` 位于新建的私有临时结果目录，脚本退出时打印路径。失败场景的完整实例另行保留，测试输出指出路径；
`scenario-result.json` 只有场景名、是否已停止等有限元数据。不要把整个失败目录上传或提交，live 场景的 raw journal
可能包含 provider opaque reasoning。成功场景在全部断言通过、host 释放、ownership 校验后清理。

显式启用真实 Codex（不是 public API key，不读取 Galatea 长期状态）：

```bash
ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE=/绝对路径/auth.json \
  /repos/focus/atelia/scripts/test_galatea_lab.sh --live
```

live 先要求离线场景通过，再运行两次 completion invocation 的 canary，并验证 metadata-only `live.jsonl`。
没有自动测试重跑；失败时先检查证据，不把重新执行包装为第一次成功。认证只通过既有
`CodexCliAuthFileCredentialProvider` 读取到内存，不复制到实例或报告。

## 隔离与生产一致性

- 保留正式 Host、真实 EventJournal/ref、SQLite 与生产 converter/parser，不以“关闭持久化”获得快速测试。
- 每个场景从合成种子创建独立根目录，配置为 SessionJournal、delegation/CharacterMemory、RecapGrid 分配独立路径。
  延迟创建的领域仍按生产规则存在或缺席，例如第一期未启用 Note/Memo binding，就不伪造 CharacterMemory store。
  helper 本身不是 OS/network sandbox；注入的 completion factory 必须受控，delegation 默认显式拒绝。
- 不提供任意生产快照 importer。复制配置不能自动改正所有绝对路径，也不能阻止旧 outbox/operation IDs 指向真实外部对象。
  如将来需要事故快照重放，另做一致性捕获、全路径重绑定与外部投递隔离，不能直接在副本上启动默认 transport。
- 测试 host 使用实例自己的 DataProtection key ring；正式子进程同样显式传
  `--Galatea:DataProtectionKeysDirectory <实例内绝对目录>`，不改写 HOME。
- 生产 completion factory 通过 DI 在解析时构造；测试替换 factory 后，不会先创建一份依赖真实账户环境的 factory。
  正常生产启动仍在 eager Host 初始化时完成原有配置检查。
- 测试关闭自动循环时，保留 shutdown-only hosted service 来等待完整 Host/SQLite 释放；不能把移除自动调度误当作
  也可以移除关闭责任。离线快照会读取锁文件，因此未释放的 owner 会真实导致场景失败，而不是被测试忽略。
- 子进程场景使用 loopback provider 和独立临时目录，不继承真实账户/proxy/provider 环境变量；helper bindings 关闭、
  enrollment 为空。进程必须已退出并被回收，才允许离线读写或清理状态。

## 证据边界与后续入口

| 场景 | 实际证明 | 不证明 |
|:--|:--|:--|
| 旧 fingerprint 修复 | 旧合同 fixture 拒绝、不发请求；一次 CLI ref 移动；原 raw/sidecar 保留；新回合完成 | 旧 binary/旧 schema 的任意迁移；原请求透明续接 |
| 进程硬杀恢复 | durable Started 后杀进程；重启不隐式重发；明确授权后继续 | 外部调用只执行一次；断电/fsync 耐久性 |
| Luna live cold reopen | 当前账户/后端的两次真实调用、持久历史跨 host 生命周期可用 | 所有模型和网络条件；模型实际利用了 opaque reasoning |

第一期主线是当前 Galatea 的 no-tools fresh turn、raw-only RecapGrid。不能为了测试 tool-loop 而把生产已退休的工具
重新注入；SessionJournal 原有工具恢复测试仍保留。下一批按价值补：真实旧版本生成的匿名种子、settled ToolResult
硬杀续接、Note/receipt 的跨进程恢复、Ready reply 与非空 RecapGrid/heartbeat 的组合场景。

评判标准包括安全性和可进展性：允许“明确 Blocked + 已验收的操作路径”，不要求所有 uncertain 故障自动成功。
参考 [runtime 恢复边界](runtime.md) 与 [CLI 回退合同](../../prototypes/SessionJournal.Cli/README.md#离线-branch-回退)。
Luna 与 reasoning 的官方接口背景见 [模型页](https://developers.openai.com/api/docs/models/gpt-5.6-luna) 和
[reasoning 指南](https://developers.openai.com/api/docs/guides/reasoning)；订阅 backend 的证据必须来自独立 live 记录。

## 2026-09-10 验收记录

`scripts/test_galatea_lab.sh Release --live`：离线场景 14/14，live 场景 1/1（两次 Luna completion invocation），
live 约 10 秒，未重跑。第一轮已有一个 native reasoning item；关闭、冷开、第二轮完成后原 native JSON 未变，
最终 checked audit 与 Idle 验证通过。公开记录只有 [有限元数据](experiments/2026-09-10-scenario-lab-live.jsonl)。
本次未导入、修改或调用真实 `gpt`/`cyber` 会话。

Galatea provider-free 全套 Debug/Release 各 841/841；Node 协议/页面测试 13/13；scoped docs checker
27 文件、0 diagnostics。全套过滤器为 `FullyQualifiedName!~LiveTests`，不把 gated live 的未执行状态计入证明。
另外重跑 SessionJournal 的已存工具结果不重执行、工具序列续接、显式 restart tool-loop 三项原有回归，3/3 通过；
这些是原有逻辑层恢复证据，不升级为本期未实现的工具进程硬杀证明。
Release Server build 为 0 warning / 0 error，runner 通过 `bash -n`，最终 `git diff --check` 通过。

开发中场景实际拦住过：测试停服后 delegation lock 尚未释放、错误的模拟 provider route、诊断清理异常覆盖问题。
这些失败的现场按合同保留；最终成功的实例已自动清理。只保留报告不等于删除生产历史。

## 第二期：自主回合的持久状态组合验收

### decision [S-LAB-PHASE2-BOUNDED] 范围与顺序

沿用合成实例与真实持久化，不建立通用场景 DSL、任意生产快照 importer 或新生产管理接口。
先验收 Note/receipt 的跨生命周期衔接，再根据这一包暴露的实际接缝决定后台调度和非空 RecapGrid
组合的扩展范围。不同工作包保持独立审阅；不把测试通过升级为外部 exactly-once 或模型已理解通知的保证。

### spec [R-LAB-NOTE-RECEIPT-CONTINUITY] Note/receipt 验收目标

- 正式 HostedService 在没有 Player 输入或 HTTP pulse 的情况下驱动自动回合。
- 第一轮保存 Note；完整停服、冷重开后下一自动轮领取 receipt；再次重开后不重复通知。
- 核对 Note identity、源 Action 与 SQLite receipt 状态，并以 raw Observation 证明实际投递次数。
- 冷重开按当前时刻重新等待 cadence，不把停机时间解释为需要补跑的 tick。
- 对 ObservationBound 后尚未追加、追加后尚未确认两个窗口使用精确边界信号；不能靠固定 sleep 猜测窗口。
- 故障现场先停止并回收 owner，再离线检查。正常 cold reopen、注入异常和真实进程硬杀分别报告，不能混称。

### derived [R-LAB-PHASE2-EVIDENCE] 实施记录

本期实施先限定为自动保存/通知的完整 Host 冷重开，以及 Observation 已追加、receipt 尚未确认的正式
Server 硬杀。receipt 的确认位于主回合完成后的 reconcile，因此 provider 收到请求可作为后一窗口的
可观测边界，不需要新生产故障开关。硬杀场景允许 HTTP Player 输入来启动这一条主路径；它不承担
无页面自动调度的证明，后者由 HostedService 场景单独验收。

绑定后尚未追加的窗口继续由现有 `GalateaNoteReceiptDeliveryTests` 的精确冷重开证明，本期不新增
test-only 可执行入口或生产 pause hook 来把它升级为硬杀证据。

非空 RecapGrid 后续应先实际生成并采用 cells，再制造 frozen request；仅注册 active recipe 的旧测试
不能当作非空 recap 恢复证据。该工作包还需接入 lab 的失败现场保留，不在本期顺带扩张。

第一包已实现并通过独立审阅：`GalateaNoteReceiptScenarioTests` 使用三个全新 Completion factory 与正式
HostedService，跨两次完整停服/重开验证相同 Note、exact receipt 与三轮 raw Observation。
时钟包含 timer；每次推进停机时间后仍重新等待完整十分钟。lab 只新增三个显式参数用于 Note binding、
enrollment 和启用正式 HostedService，不保留旧 session 或自定义 recall provider 作为跨重开状态。

启用 Note 会同时启动 DerivedInfo pump；种子用受控 helper 完成一份有效 DerivedInfo 后再关闭，避免把
尚未完成的合法辅助任务误判为提前激活。该场景不配置 Memo recall，也不证明完整 DerivedInfo 故障恢复。
主/辅助替身位于 `ICompletionClient` 边界，provider converter 的新增证据由正式进程场景单独提供。

2026-09-10 第一包 Release 定向验收：新场景与原 lab 生命周期合计 9/9，0 skipped。
首次编译发现并修复了缺失 namespace 与 nullable 警告；独立审阅补齐等待取消令牌贯通和每轮 helper
次数/用途断言。

第二包 `GalateaNoteReceiptProcessCrashTests` 复用同一自动保存种子，并从创建时就把真实进程所用连接
限定到 loopback Responses server。种子停止后只清除 enrollment，避免这一条主请求实验另起自动调度；
此后配置保持不变。正式 Server 在 provider 收到带 receipt 的请求后被硬杀，冷读同时验证 SQLite
`ObservationBound`、raw Observation exact proof 与 durable Started。

重新启动、完成正式 attach 后，用短生命周期只读 SQLite 查询证明 receipt 已经 Delivered；此时主请求仍
uncertain，默认恢复必须拒绝且不发请求。exact head 明确授权后 replay 完整相同的 provider 请求字节，
最终冷读验证同一 Note、一次 receipt、两条 Observation，以及第三次 Started（首次种子、失败前请求、
授权恢复）。恢复后的 Note extractor 仅返回无新 Note；总计两次真实 loopback 主请求、一次辅助请求。
模拟 provider 停止并排空后才作最终计数检查、允许清理成功现场。

这不是“receipt 在 SQLite 和 journal 之间原子提交”的保证，而是两个持久化边界之间的恢复协议验收。
不改变生产恢复语义，不访问真实 Codex/public API，也不证明绑定后追加前窗口硬杀、Memo recall、非空 RecapGrid
或断电/fsync 耐久性。

第二包独立审阅与尾修后，`scripts/test_galatea_lab.sh Release` 离线场景 16/16，Debug 全套 843/843。
Release 首次全套为 842/843：旧 `GalateaMemoRecallProductionVerticalTests` 的 `delegate-reply` 用例
在手工构造 Ready reply 时与后台 delegation driver 竞争 route revision；两条新增场景均通过。
该失败报告保留，不用重复运行掩盖；后续按独立测试协调修复处理，再记录最终全套结果。

附加尾修仅修改该旧测试的 fixture：注入受控 delegation transport，种子只 Capture、Signal 并限时等待
目标 dispatch Ready，由真实 supervisor/driver 独占 binding、start、completion 状态迁移。删除手工
写入中间状态的第二个 writer，保留原 selector、typed trigger、origin/recall barrier 断言；不通过重试
revision 或关闭生产校验来消除偶发失败。该修复已通过独立审阅。

### derived [R-LAB-PHASE2-ACCEPTANCE] 2026-09-10 最终验收

- 一键离线 lab：16/16；不启用 `--live`，两条新增场景均纳入原 runner。
- 修复后的 Memo recall 测试组连续三次各 6/6，分别保留报告，属于修复后的稳定性检查。
- 最终 Galatea provider-free 全套：Debug、Release 各 843/843，0 skipped；仍使用
  `FullyQualifiedName!~LiveTests`，不把未运行的 live 当作通过。
- Node HTTP/SSE/follower：13/13；Release Server build：0 warning / 0 error。
- scoped docs checker：27 文件、0 diagnostics；runner `bash -n` 与 `git diff --check` 通过。

首次失败报告与修复后结果分别保留为 `galatea-release.trx`、`galatea-release-final.trx`，
最终 Debug 为 `galatea-debug-final.trx`，均在本次 runner 打印的私有结果目录中。
本期没有修改生产代码、真实 `gpt`/`cyber` 状态或启动部署实例，也没有调用真实外部 LLM。

下一独立包优先验收“已实际采用非空 recap 的 frozen request 冷恢复”；Memo recall 与 receipt 的三方
联合恢复、绑定后追加前硬杀及 SessionJournal tool-loop 硬杀仍未包含在本期证据中。

## 第三期：非空 RecapGrid 的冻结请求恢复

### decision [S-LAB-PHASE3-BOUNDED] 范围与边界

本期只连接“实际生成并采用非空 recap → 冻结请求 → 冷恢复 → 下一 fresh turn”。
复用合成实例、正式维护路径和现有恢复授权，不引入生产快照 importer、通用场景 DSL、生产 pause hook，
也不为测试重新注入已退休的 fresh-turn 工具。主模型与 recap helper 均使用受控替身或 loopback provider。
Memo recall/receipt 三方组合和外部 LLM canary 不在本期范围。

### spec [R-LAB-NONEMPTY-RECAP-RECOVERY] 验收标准

- 种子必须通过正式 RecapGrid 维护生成非空 cells，并证明内容实际进入主请求；仅有 active recipe、
  store 中存在 cells 或页面展示摘要都不能替代请求采用证据。
- 冻结前后以 durable Prepared 的 exact context 和 canonical request commitment 核对请求。
  恢复必须复用冻结内容，不得通过重新构建 recap 得到一份“看起来相同”的新请求。
- Prepared 尚未 Started 的确定性故障注入与 Started 后的真实进程硬杀分别报告；后者必须观察请求
  到达 loopback provider 再硬杀并回收进程，不以 sleep 猜测持久化窗口。
- 冷恢复保持现有 uncertain 授权边界；拒绝隐式重试时不得出现额外 provider invocation。
- 恢复完成后必须继续一个 fresh turn，并最终冷读确认 Idle 与新增完成回合，验证恢复后的可进展性。
- 所有 owner 停止后才能离线读写；通过全部断言才清理合成实例，失败保留现场与有限诊断。

### derived [R-LAB-PHASE3-EVIDENCE] 实施与验收记录

改动前 Release 定向基线：原 rolling Host/operator 6/6，原 lab/lifecycle 16/16。
首次新场景编译通过，旧 rolling 回归 5/5；两个冷恢复场景均在种子阶段失败：第一轮主调用成功，
但 recap 调用是 0 而非假定的 2。失败现场冷读确认 Timeline 仍为零行，cadence policy 未被覆盖。
这是新 fixture 对首轮维护时序的错误假设，不是已经证实的生产恢复故障；保留首轮失败报告，
后续修正种子后另记验收，不通过放宽调用计数或重复运行掩盖。

两步种子通过后，第二次定向运行在冻结前维护得到 `MaintenanceContinuation`。冷读合成 SQLite 显示
Timeline 已有三行，grid 仅完成两行：`targetHistoryLoad=1` 会把 Observation 和 Action 分别封行，
不能把“一个主回合”当成“一个 recap row”。因此后续维护计划按精确 row/prior generation 编排，
每个 row 恰好两列，恢复期仍为零维护；不改生产 cadence 或容忍任意额外调用。

第一包 `GalateaRecapRecoveryScenarioTests` 已完成：两个正式 HTTP 种子回合（第一轮 raw-only，第二轮
采用 V1 两列）；通过现有 SessionJournal failpoint 分别生成 Prepared/Started 边界，冻结前使用真实
Online candidate/lifecycle 构建 V2/V3，最终采用 V3。全新 Host/factory 恢复只允许一次主调用，
canonical bytes 与 durable commitment 相符，Prepared 地址与原 audit payload 不变，整个
`derived`/`control` 文件快照不变。再次冷重开后 fresh 构建 V4/V5 并采用 V5，最终四个完成回合、Idle。
每行两列、完整前代 prior 和阶段调用配额均为精确约束，不以调用总数猜测列/代数归属。

共享 `GalateaRecapFixture` 从旧 rolling Host 测试机械抽取 provisioning；原测试仍复用同一路径与原断言。
lab 仅透传现有 `provisionRawOnly` 参数，新种子在首次 Host 初始化之前创建本地 profile/routes 与派生存储。
独立审阅及行计划尾修后，Release 两个新冷恢复用例 2/2。

第二包 `GalateaRecapProcessCrashTests` 复用同一两步种子。正式 Server 的第一次主请求前，真实
Responses client 经本地 HTTP 完成 V2/V3 四次 recap；主请求在正式 Observation/Action carriers
携带 V3 后被挂起，随后硬杀并回收子进程。离线严格重构证明非空 exact inputs 与 canonical commitment，
raw audit 为三个 Observation、三个 Prepared、三个 Started、两个 Action。

第二个正式子进程先拒绝未授权恢复；授权后发送完整相同的主请求 wire bytes，期间禁止任何 recap 调用。
完成后再停进程，冷读核对同一 Prepared/Observation、canonical bytes、ControlHead 与 cell digests。
第三个子进程的 fresh turn 正常生成 V4/V5 并采用 V5，最终冷读四个完成回合、Idle，以及
四个 Observation、四个 Prepared、五个 Started、四个 Action。原始输入与 Action 均不含 helper marker，
避免把历史原文回放误认成 recap 采用。

loopback provider 严格限定三个主请求和八个 recap 请求；两列 prior、代数与阶段逐一匹配，停止并排空
provider 后才最终核对计数、清理成功现场。部署配置与真实会话不参与实验。独立审阅通过，
Release 首次进程场景运行 1/1；它不证明断电/fsync 耐久性、自动调度或外部调用 exactly-once。

### derived [R-LAB-PHASE3-ACCEPTANCE] 2026-09-10 最终验收

- 一键 `scripts/test_galatea_lab.sh Release`：离线 19/19，0 skipped；未启用 `--live`。
- Galatea provider-free 全套：Release、Debug 各 846/846，0 skipped；过滤器仍为
  `FullyQualifiedName!~LiveTests`，不把未执行的 live 算作通过。
- Node HTTP/SSE/follower：13/13；scoped docs checker：27 文件、0 diagnostics。
- Release Server build：0 warning / 0 error；runner `bash -n` 与最终 `git diff --check` 通过。
- 两包均经独立代码审阅、动态问题尾修与最终文档证据边界复核；仅测试与文档变更。

定向首次失败、种子修正后的第二次失败、行计划修正后的成功及最终全套报告分别保留于
`/mnt/wsl/fast/tmp/atelia-galatea-phase3-results.J5mbPj`；一键 runner 的独立结果目录为
`/mnt/wsl/fast/tmp/atelia-galatea-lab-results.wc1zW9`。这些是本次本机诊断路径，不是 CI 固定路径。
未修改真实 `gpt`/`cyber` 状态、部署配置或访问真实外部 LLM。成功实例已按 lab 合同清理，失败实例保留。

本期到此收口；后续按实际风险另评估 Memo recall/receipt/RecapGrid 三方组合、绑定后追加前硬杀或
SessionJournal tool-loop 硬杀，不以当前通过结果声称所有生产 E2E 必然成功。
