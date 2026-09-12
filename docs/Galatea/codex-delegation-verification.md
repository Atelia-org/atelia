# Codex delegation 验证

本文把可重复的 live canary 操作与已带日期的历史证据分开。它只验证指定链路，不能推导 app-server/provider 的 exactly-once 承诺；运行时语义见 [运行时机制](runtime.md)，当前实现/测试状态见 [delegation 重构状态](codex-delegation-refactor-status.md)。

## 可重复的 V4 transport canary

`GalateaCodexDelegationLiveTests.DurableV4_EnsureStartInspectCompletesInCleanRepo` 是显式 opt-in 的 real app-server V4 transport canary。默认 test discovery 会在读取配置、创建临时目录和启动 sidecar 前 skip。运行它会使用机器上的 Codex 登录和 provider/auth 网络。

在仓库根目录执行：

```bash
npm --prefix local-codex-mcp run build
export ATELIA_GALATEA_CODEX_DELEGATES_CONFIG="$(realpath prototypes/Galatea/.atelia/galatea/delegates.json)"
codex_command="$(jq -r '.sidecar.codexCommand' "$ATELIA_GALATEA_CODEX_DELEGATES_CONFIG")"
"$codex_command" login status
export ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE=1
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName=Atelia.Galatea.Server.Tests.GalateaCodexDelegationLiveTests.DurableV4_EnsureStartInspectCompletesInCleanRepo'
```

该测试的目标是 isolated clean repository 中的 V4 `ensure-binding → exact start-turn → inspect` 链。它把 route 固定为 research、关闭 local-command network 与 hosted tools，并将 allowed root 和两个执行请求的 cwd 指向随机临时 Git repository。即使本地临时目录清理，Codex 的 thread/turn 仍是外部持久状态。

## 用户 home 的实际文件验证

`npm --prefix local-codex-mcp run test:galatea-homes:live` 显式运行真实 sidecar V4 canary：从 `/` 启动共享进程，两个临时用户目录并发写同名相对文件；同一 thread 冷重启和热 resume 后切换 home；旧目录删除且移出 allowedRoots 后读取原任务结果。四个模型 turn 完成后，官方 `thread/turns/list` 核对两个 thread 分别只有 3/1 个 turn。测试使用隔离目录和新建测试 thread；默认 `npm test` 跳过它。实际执行结果见 [home 实施记录](user-home-design.md)。

C# Supervisor/driver/transport 测试覆盖 per-user 目录接线、共享释放、原 thread 重开及 active-first；旧格式夹具测试覆盖本地 V1→V2 升级与零重复 start。它们与上述实际文件验证分别说明各层行为。

此 canary 不构造 Galatea host/SQLite baseline，也不覆盖 accepted 后 host restart、双信 FIFO、durable reply lease 或完整邮件投递链。完整 durable real-provider vertical 是独立的后续 operational verification。

## 历史证据（不得作为当前验证）

- **2026-08-27**：通过的 real app-server canary 属于已删除的 process-local/V1 owner。V1 coordinator/sidecar/Node entry 和 runbook 已 hard cut；该结果不能证明当前 SQLite/V3 链。
- **2026-08-28**：旧 V2 build 曾按类似 gate PASS 1/1，记录的行为包括 empty owned thread、pre-start NotFound、一次 unique start、C# tombstone 拒绝重复 dispatch，以及 inspect 获得匹配随机 token 的 Completed。它只说明当时的 V2 transport、fixed-thread ownership 和 once-start fencing；不是 V3 live E2E 证据。
- **同日 ignored 开发实例 `cyber`**：曾完成不调用 provider 的 production smoke：writable attach 发布 SQLite baseline，HTTP login `302`、recent `200/6`，停服后 writer lock 释放且 SQLite `quick_check=ok`，冷重启后 recent 仍 `200/6`。该结果只支持当时该机器的 baseline、lock 与 cold reopen；不证明 Codex dispatch/reply。

旧验证不能替代当前 V4 canary，也不能证明 provider 或 app-server 的 exactly-once 行为。最新 home 改动见 [实施记录](user-home-design.md)，此前阶段见 [delegation 重构状态](codex-delegation-refactor-status.md)。
