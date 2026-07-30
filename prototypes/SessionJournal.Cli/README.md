# SessionJournal.Cli

`SessionJournal.Cli` 是当前 SessionJournal 的迁移、离线诊断与 DerivedRecap
composition root。它组合：

- raw `Atelia.SessionJournal`；
- `SessionJournal.DerivedRecap.Store`；
- `SessionJournal.DerivedRecap.Planner`；
- `SessionJournal.DerivedRecap.Maintainers`；
- Completion provider。

CLI 不依赖已删除的 `SessionJournal.DerivedMemory`。旧 `ChatSession` repo 的读取和导出由
[`ChatSession.LegacyExportCli`](../ChatSession.LegacyExportCli/README.md)负责；两个工具只通过
`atelia.chat-session.legacy-upgrade-export.v1` JSON 交换数据。

## 通用约束

- branch-local 命令必须显式给出 `--branch`；branch name 只用于选择，durable identity 是
  exact `BranchRefId`。
- 所有命令拒绝未知 option 和重复 scalar option。
- output/report/call-log 必须位于 input repo 外；路径链拒绝 symlink/reparse point，输出使用同目录
  临时文件 atomic publish。
- raw events 始终是 correctness source。Recap Store 是可删除、可重建的 sidecar；CLI 不向 raw
  journal 写入 recap identity。
- `--connection` 可省略并由 connections registry 解析默认项。

## recap 命令族

```text
recap create
recap inspect
recap run
recap resume
recap restore
recap abandon-building
recap reset
```

所有子命令都要求 `--input <repo-dir> --branch <name>`。Store 不会由 `run`、online 或读取路径自动
创建；首次使用必须显式执行 `recap create`。同样不存在自动 reset 或“一键 reset-and-rebuild”。

### recap create

显式创建当前 exact `BranchRefId` 的空 Store：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap create --input <repo-dir> --branch main \
  [--report-json <path-outside-repo>]
```

该命令不运行 Planner、Maintainer 或 LLM，也不做 catch-up。重复 create 遵守 Store 自身的
create contract；不会借此覆盖或重置既有 Store。

### recap inspect

同时检查 exact anchor 上的 Building 和 Published membership：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap inspect --input <repo-dir> --branch main \
  --anchor ej1:<canonical-event-address> \
  [--report-json <path-outside-repo>]
```

Published 报告把 membership 的 `Present / Absent / Invalid / StoreUnavailable` 与
`restoreEligibility`、per-block capability 分开。Published directory 已存在但 payload 损坏时仍是
exact ordinal member，不会被误报成 Missing。`inspect` 是只读操作，成功完成检查返回 0；参数、路径、
raw 或 Store 读取错误返回 1。

### recap run

使用固定 production catalog（world-understanding/Observation 在前、
autobiography/Action 在后）和 bounded `MaintainAll` policy，执行一次
plan-or-resume-and-publish：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap run --input <repo-dir> --branch main \
  --connections <connections.json> \
  [--connection <id>] \
  [--call-log-dir <path-outside-repo>] \
  [--report-json <path-outside-repo>]
```

未达到 trigger 时返回 `NoBuild`；需要创建 Building 时，Planner 先冻结 exact source、route、
prior context 与 limits，再调用 Maintainers。已有 Building 时只补缺失或损坏的 block-local work；
healthy final block 不重做。成功 Publish 后才进入 strict ordinal。

### recap resume

只恢复一个 exact Building 的 frozen plan：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap resume --input <repo-dir> --branch main \
  --anchor ej1:<canonical-event-address> \
  --connections <connections.json> \
  [--connection <id>] \
  [--call-log-dir <path-outside-repo>] \
  [--report-json <path-outside-repo>]
```

`resume` 不重新规划、不改 roster/mode/source/route/prior，也不把 partial Building 当作 Published。
Building missing/invalid 是 typed unavailable，而不是创建新 Building。

### recap restore

只修复同一个 Published directory 内的缺失或损坏 component：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap restore --input <repo-dir> --branch main \
  --anchor ej1:<canonical-event-address> \
  --expected-raw-head ej1:<canonical-event-address> \
  --connections <connections.json> \
  [--connection <id>] \
  [--call-log-dir <path-outside-repo>] \
  [--report-json <path-outside-repo>]
```

`--expected-raw-head` 是显式 optimistic fence。Restore 不改变 Published membership、strict
ordinal、frozen plan 或 admission anchor；无法从 frozen input/checkpoint恢复时返回
`Unavailable`，不 replan、不扫描更旧 set。

### recap abandon-building

把 exact unpublished Building 原子移动到 Store-owned quarantine：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap abandon-building --input <repo-dir> --branch main \
  --anchor ej1:<canonical-event-address> \
  [--report-json <path-outside-repo>]
```

`Quarantined` 与 `AlreadyAbsent` 返回 0；`PublishedConflict` 与 `Unavailable` 返回 2。该命令不删除或
修改 Published directory。

### recap reset

显式隔离并重建整个 branch-local Store root：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap reset --input <repo-dir> --branch main \
  --confirm-ref <exact-lowercase-branch-ref-id> \
  [--report-json <path-outside-repo>]
```

`--confirm-ref` 必须逐字匹配当前选中 branch 的 exact RefId，并在任何 Store mutation 前校验。
reset 后的 catch-up 仍需显式执行一次或多次 `recap run`。

### recap execution 状态、退出码与报告

`run`、`resume`、`restore` 的稳定结果映射为：

| 结果 | 退出码 | 含义 |
|---|---:|---|
| `Published` / `Restored` / `NoBuild` | 0 | 操作完成，或当前无需建立新 set |
| `Unavailable` / `BlockFailed` | 2 | 稳定的 Store、frozen plan 或 block failure |
| `Retryable` | 3 | raw head/CAS 等 optimistic boundary 已改变，可在重新检查后重试 |
| 参数、路径或未分类运行错误 | 1 | 命令级失败 |

JSON report 是 content-free operation record：只包含 schema、operation、branch/ref、raw head、
anchor/block、typed status/code/defect codes，以及 call-log 数量和目录。它不复制 recap 正文、
FrozenInput、PriorContext、prompt/response、provider error body、state token、内容 hash或 secret。

## run-online-turn

`run-online-turn` 是 phase-first online composition。它先打开 raw SessionJournal 并检查 durable
execution phase，再决定是否需要 Recap：

| 初始 phase | `--message` | Recap Store / Planner / Maintainers |
|---|---|---|
| `Idle` / `TurnFailed` | 必须提供 | 需要；Store 必须已显式 create |
| `AwaitingAgentAction` + `ObservationAccepted` | 必须省略 | 需要；完成已经提交的 observation |
| `AwaitingCompletionDispatch`（Prepared） | 必须省略 | 完全不打开、不创建、不修复 Store |
| `AwaitingCompletion`（Started） | 必须省略 | 完全不打开、不创建、不修复 Store |
| tool continuation | 必须省略 | 当前 CLI 无 exact tool runtime，显式拒绝 |

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  run-online-turn \
  --input <repo-dir> \
  --branch main \
  --connections <connections.json> \
  --output <path-outside-repo>/online-turn.json \
  [--message "continue"] \
  [--connection <id>] \
  [--call-log-dir <path-outside-repo>] \
  [--maximum-canonical-request-bytes <positive-int64>] \
  [--uncertain-recovery refuse|restart-new-attempt]
```

candidate ordinal 不是 CLI flag；它只来自 selected branch governing `RuntimeConfigSetup` v2 的
`derivedContext.nthPrevious`，`0` 表示最近 Published set。selection 是 strict ordinal：exact slot
损坏时 bounded Restore 同一 slot，不跳过、不重编号。

`--maximum-canonical-request-bytes` 是 final canonical request JSON 的精确 UTF-8 byte guard，不是
provider tokenizer、模型 context-window 或 fallback policy。`--uncertain-recovery` 默认 `refuse`；
只有 operator 明确接受潜在重复 provider 调用时，才可选 `restart-new-attempt`。

Store 缺失时，需要新 request 的 phase 在 append Observation、创建 client/call-log 或调用 LLM 前
失败；CLI 不会 auto-create/reset。若 lifecycle/backpressure、candidate 或 request-size preflight
失败，尚未 append 的 Observation 保持未提交。Prepared/Started recovery 则以 Prepared exact
request 为唯一真源，对 Store 是 zero-touch。

成功返回 0；参数、unsupported phase、not-ready、Store/Completion 或路径失败返回 1。online JSON
report 同样 content-free，只包含 branch/ref、head、最终 phase、provider/API/model、Action 文本
SHA-256 与 error count；完整 request/action 只存在于明确配置的 call log。

## import-legacy-json

把 `ChatSession.LegacyExportCli export-json` 生成的 JSON 导入新 SessionJournal repo：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  import-legacy-json \
  --input <legacy-export.json> \
  --output <new-repo-dir> \
  [--force] \
  [--report-md <path-outside-repo>]
```

导入只保留 raw setup、observation、agent action；旧 compaction/recap 只计数并跳过。未知 event 与
无法无损表达的旧 tool transcript fail fast。`--force` 使用同级 staging repo 完整导入、reopen
验证后再替换 exact target，不把“导出成功”当作 importer 语义验收。

## validate

严格、只读验证一个 existing active branch：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  validate --input <repo-dir> [--branch main] \
  [--report-json <path-outside-repo>]
```

省略 `--branch` 时默认 `main`。命令由 `SessionJournal.Offline` 检查 raw chain、historical Prepared
commitments、forward operational legality、tail phase 和 governing setup；不会修复或截断
raw/refs，也不读取 Recap Store。

## llm-smoke

发送一次最小 Completion 请求，用于验证 connection/provider/call-log：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  llm-smoke --connections <connections.json> \
  [--connection <id>] \
  [--call-log-dir <path-outside-repo>] \
  [--message <text>]
```
