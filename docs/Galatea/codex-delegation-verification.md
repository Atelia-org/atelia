# Codex delegation 验证

> **当前验收入口（2026-09-16）**：当前 delegation SQLite V5、sidecar wire V6 的格式合同见[结构化输入方案](structured-input-rendering-design.md)与[运行时](runtime.md)。最新真实迁移、Codex／Player 调用、冷审计及服务恢复证据集中于[迁移验收](player-character-migration-validation.md)，其中保留首次 SidecarReady 前失败及原因未确认的限制。下文按日期保留旧版本测试、故障与 canary 记录，不将历史成功改写为新版或完整实例验收。

本文把可重复的 live canary 操作与已带日期的历史证据分开。它只验证指定链路，不能推导 app-server/provider 的 exactly-once 承诺；运行时语义见 [运行时机制](runtime.md)，旧阶段完成记录见 [delegation 重构状态](codex-delegation-refactor-status.md)。

## 2026-09-14 空启动投影修复验证

[故障与辩证裁决](codex-delegation-live-observation.md)：关联的 sparse start response 现在建立 live 身份；后续 user item 验证一致性。冷恢复和 SQLite V3 无变更。

- `npm run check` 通过；`npm test`：131 passed、2 explicit live skipped、0 failed。默认 fake start/started 使用空 items，completed 使用不含 userMessage 的 summary；覆盖正常/缺少 user 通知的 10 次 live Running、零历史分页、早到终态、迟到 final、身份冲突、容量限制和 generation 清除。
- Galatea Driver 与 BoundedRecoveryVertical 定向测试：65 passed、0 skipped、0 failed。覆盖 live 清零、persistent 失败耗尽、单次发送和唯一回信。使用真实 SQLite 与生产 Driver；该集合的外部 transport 为测试替身。
- 固定 `0.154.0-alpha.3` 原生 app-server canary：旧 dist 在第一次检查因 `source=persistent` 失败；修复后通过 10 次 live Running → live Completed，历史分页 0 次，本地合成 Responses 请求 1 次。

原生 canary 可重复运行：

```bash
cd local-codex-mcp
npm run build
npm run canary:live-observation
```

它经过生产 CodexBackend/Client 和 pinned app-server，在临时 HOME/CODEX_HOME 中写入合成配置与明确的假凭证，只连接 localhost SSE fixture。以事件 barrier 延迟 final，等待真实 user/started 通知，不依赖 HTTP/stdout 的到达顺序。结束后回收进程和临时目录；不读取真实 auth/config/session，不调用真实模型，不重发现场邮件。这份证据证明实际 app-server 协议与桥接实现一致，不代表现场旧任务已恢复或真实 provider E2E 已通过。

## 2026-09-14 有限恢复重构本地验证

本轮按[批准方案](codex-delegation-recovery-refactor-plan.md)实施 wire V5、SQLite V3 和有限恢复。全部验证关闭 live gates；没有迁移真实 `.atelia` 用户库或调用真实模型。

- Node `npm run check` 通过；`npm test`：116 passed、2 explicit live skipped。包括 warm/cold 空线程、受控发送阶段、启动回应丢失、总分页截止和截止后不再发送。
- 生产 Debug build：0 warnings / 0 errors；完整 Debug 与 Release 非 live 集分别为 971 passed、1 explicit live skipped、0 failed。
- Store 与 V1/V2→V3 migration 集：82/82 passed。包含绑定预算归属 FIFO 首信、原 terminal/notice/lease 与 baseline 保留、坏旧形状拒绝、耗尽旧任务首 pulse 零 RPC、本地结算事务中断与严格重开。
- 浏览器 Node 测试：17/17 passed。状态提示表达有限恢复，API 字段保持原样。
- `check_session_journal_docs.py`：31 files、0 diagnostics；`git diff --check` 通过。

新增 `GalateaBoundedRecoveryVerticalTests` 使用真实 SQLite、Driver、C# V5 进程 transport 和 SessionJournal reply lease。fake sidecar 模拟结果不明耗尽、重绑定与后续成功，两封回信各消费一次；另一例覆盖 cold empty thread 的 NotDispatched 安全重试。它们没有调用真实 Codex/provider，不能代替下面的 opt-in canary。

交叉审阅关闭的关键问题：原生进程启动异常的 claim 释放、C# 总截止覆盖 ready/write、耗尽旧 active 首 pulse 终结、Queued 明确失败遇 inbox 满时持久保存结算决定，以及 local terminal 的 late result 不影响新 route。正常远端矛盾 terminal 仍保留原有隔离规则。

部署时同步构建 Node/C# 并按[离线升级说明](codex-delegation-operator-recovery.md)显式升级 V1/V2 库；运行时不会偷偷迁移。主模型调用恢复、RecapGrid/hash 清理、Codex 升级均未混入此工作包。

## 可重复的 V5 transport canary

`GalateaCodexDelegationLiveTests.DurableV5_EnsureStartInspectCompletesInCleanRepo` 是显式 opt-in 的 real app-server V5 transport canary。默认 test discovery 会在读取配置、创建临时目录和启动 sidecar 前 skip。运行它会使用机器上的 Codex 登录和 provider/auth 网络。

在仓库根目录执行：

```bash
npm --prefix local-codex-mcp run build
export ATELIA_GALATEA_CODEX_DELEGATES_CONFIG="$(realpath prototypes/Galatea/.atelia/galatea/delegates.json)"
codex_command="$(jq -r '.sidecar.codexCommand' "$ATELIA_GALATEA_CODEX_DELEGATES_CONFIG")"
"$codex_command" login status
export ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE=1
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName=Atelia.Galatea.Server.Tests.GalateaCodexDelegationLiveTests.DurableV5_EnsureStartInspectCompletesInCleanRepo'
```

该测试的目标是 isolated clean repository 中的 V5 `ensure-binding → exact start-turn → inspect` 链。它在测试用 Codex 原生配置中限定工具权限，并将 allowed root 和两个执行请求的 cwd 指向随机临时 Git repository。即使本地临时目录清理，Codex 的 thread/turn 仍是外部持久状态。

## 用户 home 的实际文件验证

`npm --prefix local-codex-mcp run test:galatea-homes:live` 显式运行真实 sidecar V5 canary：从 `/` 启动共享进程，两个临时用户目录并发写同名相对文件；同一 thread 冷重启和热 resume 后切换 home；旧目录删除且移出 allowedRoots 后读取原任务结果。四个模型 turn 完成后，官方 `thread/turns/list` 核对两个 thread 分别只有 3/1 个 turn。测试使用隔离目录和新建测试 thread；默认 `npm test` 跳过它。实际执行结果见 [home 实施记录](user-home-design.md)。

C# Supervisor/driver/transport 测试覆盖 per-user 目录接线、共享释放、原 thread 重开及 active-first；旧格式夹具测试覆盖本地 V1→V2 升级与零重复 start。它们与上述实际文件验证分别说明各层行为。

此 canary 不构造 Galatea host/SQLite baseline，也不覆盖 accepted 后 host restart、双信 FIFO、durable reply lease 或完整邮件投递链。完整 durable real-provider vertical 是独立的后续 operational verification。

## 历史证据（不得作为当前验证）

- **2026-08-27**：通过的 real app-server canary 属于已删除的 process-local/V1 owner。V1 coordinator/sidecar/Node entry 和 runbook 已 hard cut；该结果不能证明当前 SQLite/V3 链。
- **2026-08-28**：旧 V2 build 曾按类似 gate PASS 1/1，记录的行为包括 empty owned thread、pre-start NotFound、一次 unique start、C# tombstone 拒绝重复 dispatch，以及 inspect 获得匹配随机 token 的 Completed。它只说明当时的 V2 transport、fixed-thread ownership 和 once-start fencing；不是 V3 live E2E 证据。
- **同日 ignored 开发实例 `cyber`**：曾完成不调用 provider 的 production smoke：writable attach 发布 SQLite baseline，HTTP login `302`、recent `200/6`，停服后 writer lock 释放且 SQLite `quick_check=ok`，冷重启后 recent 仍 `200/6`。该结果只支持当时该机器的 baseline、lock 与 cold reopen；不证明 Codex dispatch/reply。

旧验证不能替代当前 V5 canary，也不能证明 provider 或 app-server 的 exactly-once 行为。最新 home 改动见 [实施记录](user-home-design.md)，此前阶段见 [delegation 重构状态](codex-delegation-refactor-status.md)。
