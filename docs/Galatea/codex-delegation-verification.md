# Codex delegation 验证

本文把可重复的 live canary 操作与已带日期的历史证据分开。它只验证指定链路，不能推导 app-server/provider 的 exactly-once 承诺；运行时语义见 [运行时机制](runtime.md)，当前实现/测试状态见 [delegation 重构状态](codex-delegation-refactor-status.md)。

## 可重复的 V3 transport canary

`GalateaCodexDelegationLiveTests.DurableV3_EnsureStartInspectCompletesInCleanRepo` 是显式 opt-in 的 real app-server V3 transport canary。默认 test discovery 会在读取配置、创建临时目录和启动 sidecar 前 skip。运行它会使用机器上的 Codex 登录和 provider/auth 网络；先确认这符合当前操作授权。

在仓库根目录执行：

```bash
npm --prefix local-codex-mcp run build
export ATELIA_GALATEA_CODEX_DELEGATES_CONFIG="$(realpath prototypes/Galatea/.atelia/galatea/delegates.json)"
codex_command="$(jq -r '.sidecar.codexCommand' "$ATELIA_GALATEA_CODEX_DELEGATES_CONFIG")"
"$codex_command" login status
export ATELIA_RUN_GALATEA_CODEX_DELEGATION_LIVE=1
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj --no-restore -m:1 -nr:false --filter 'FullyQualifiedName=Atelia.Galatea.Server.Tests.GalateaCodexDelegationLiveTests.DurableV3_EnsureStartInspectCompletesInCleanRepo'
```

该测试的目标是 isolated clean repository 中的 V3 `ensure-binding → inspect → exact start-turn → inspect` 链。它应把 route 固定为 research、关闭 local-command network 与 hosted tools，并将唯一 allowed root/cwd 指向随机临时 Git repository。即使本地临时目录清理，Codex 的 thread/turn 仍是外部持久状态；不要把前者清理视为后者删除。

此 canary 不构造 Galatea host/SQLite baseline，也不覆盖 accepted 后 host restart、双信 FIFO、durable reply lease 或完整邮件投递链。完整 durable real-provider vertical 是独立的后续 operational verification。

## 历史证据（不得作为当前验证）

- **2026-08-27**：通过的 real app-server canary 属于已删除的 process-local/V1 owner。V1 coordinator/sidecar/Node entry 和 runbook 已 hard cut；该结果不能证明当前 SQLite/V3 链。
- **2026-08-28**：旧 V2 build 曾按类似 gate PASS 1/1，记录的行为包括 empty owned thread、pre-start NotFound、一次 unique start、C# tombstone 拒绝重复 dispatch，以及 inspect 获得匹配随机 token 的 Completed。它只说明当时的 V2 transport、fixed-thread ownership 和 once-start fencing；不是 V3 live E2E 证据。
- **同日 ignored 开发实例 `cyber`**：曾完成不调用 provider 的 production smoke：writable attach 发布 SQLite baseline，HTTP login `302`、recent `200/6`，停服后 writer lock 释放且 SQLite `quick_check=ok`，冷重启后 recent 仍 `200/6`。该结果只支持当时该机器的 baseline、lock 与 cold reopen；不证明 Codex dispatch/reply。

旧验证不能替代当前 V3 canary，也不能证明 provider 或 app-server 的 exactly-once 行为。最新 provider-free gate、显式 live skip 与实现范围请以 [delegation 重构状态](codex-delegation-refactor-status.md) 为准。
