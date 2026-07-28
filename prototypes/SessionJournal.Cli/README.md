# SessionJournal.Cli

`SessionJournal.Cli` 是 `SessionJournal` 的离线开发与迁移工具，也是
`SessionJournal.Maintainers` 的 composition root。它依赖 SessionJournal contracts、
concrete maintainer profiles 和 Completion provider，但不依赖旧 `ChatSession`
程序集。

旧 `ChatSession` repo 的读取和导出由相邻的
[`ChatSession.LegacyExportCli`](../ChatSession.LegacyExportCli/README.md)
负责；两个工具只通过版本化 JSON schema
`atelia.chat-session.legacy-upgrade-export.v1` 交换数据。

## import-legacy-json

把 `ChatSession.LegacyExportCli export-json` 生成的 JSON 导入新的
`SessionJournal` repo：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- import-legacy-json \
  --input gitignore/migrations/<session>.json \
  --output gitignore/session-journal/<session> \
  --report-md gitignore/session-journal/<session>-import.md
```

导入只保留 raw facts：initial setup、observation、agent action 和 system
prompt setup。旧 compaction/recap 是可重建的 derived 信息，因此只计数并跳过。
未知 event（包括 `revert-turn`）会 fail fast，不会猜测 SessionJournal 的
Parent/ref 语义。

当前 importer 接受普通 observation/action 历史与 system-prompt update。带
tool-call/tool-results 的旧回合会在触碰 `--force` 目标前 fail fast：
SessionJournal 的 tool execution 需要 started/result/checkpoint/correlation
等 raw 事实，不能把旧 transcript 直接伪装成新 execution wire。

目标目录必须是不存在或为空的目录。`--force` 会先在目标的同级 staging
repository 完整导入并 reopen 验证，再替换精确目标；发布失败时会尝试恢复旧目录。
input/output/report 的路径链都拒绝 symlink/reparse point，且 output 不得包含
input、report 不得覆盖 input 或位于 output repo 内。报告通过同目录临时文件
atomic publish。

## run-memory-maintainer

从 `SessionJournalEngine.ReplayHistory()` 读取带 raw address 的历史，按 runner-local
synthetic sliding-prefix policy 触发一个 rewrite maintainer，并输出 JSONL、Completion
call log 和 `derived/recaps/v1/` artifact：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- run-memory-maintainer \
  --input gitignore/session-journal/<session> \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --profile autobiographical-rewrite \
  --threshold-tokens 24000 \
  --max-epochs 1 \
  --output gitignore/backtest/<run>/result.jsonl \
  --call-log-dir gitignore/backtest/<run>/calls
```

可用 profile：

| profile | target |
| --- | --- |
| `autobiographical-rewrite` | `action/roleplay.first-person-autobiography` |
| `world-understanding-rewrite` | `observation/roleplay.world-understanding` |

可用覆盖参数为 `--system-prompt <path>` 与 `--prompt <path>`。输出与 call-log
目录必须位于 input repo 外，JSONL 使用同目录临时文件 atomic publish。

当前 runner 从 raw history 起点和空 `MemoryPack` 开始 full replay；目标 lineage
必须为空。CLI 拆分同时把 producer identity 更新为
`SessionJournal.Cli/run-memory-maintainer`，所以旧 BacktestCli 生成的 derived store
不会被当成当前 producer 的可续写状态。若要重跑，只删除精确的
`<repo>/derived/recaps/v1/`；raw events 仍是唯一 correctness source。

该命令是 maintainer 开发入口，不是最终 shared epoch planner。它当前运行
`RewriteMemoryBlockMaintainer`，profile 来自 `SessionJournal.Maintainers`；未来 concrete
maintainer 类型可在此 composition root 增加 factory/descriptor，而不进入 raw core。

## validate

严格、只读验证 SessionJournal：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- validate \
  --input gitignore/session-journal/<session> \
  --report-json gitignore/session-journal-validation.json
```

report 必须在 repo 外。validator 不修复或截断 raw/refs。

## DerivedMemory ArtifactSet 运维命令

以下命令只操作可重建的 `derived/` 子系统。它们不会向 raw SessionJournal
追加 event，也不会创建 Completion client。所有命令都拒绝未知 option；标量 option
重复出现也会 fail fast。`--report-json` 必须位于 input repo 外，并通过同目录临时文件
atomic publish。

role/target 使用 `role=carrier/block-key`，其中 carrier 只能是 `system`、
`observation` 或 `action`。block key 可以继续包含 `/`。member 使用
`role=artifact-id`。通常可写 `--key value`；若合法 value 本身以 `--` 开头，
使用 conventional inline form `--key=--value`（例如
`--required-role=--role=observation/--block`），避免与下一个 option 混淆。

### publish-derived-artifact-set

从已经存在的 exact artifacts 发布 immutable coherent set：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  publish-derived-artifact-set \
  --input gitignore/session-journal/<session> \
  --lineage main \
  --coherence-group roleplay.default \
  --policy-id roleplay-memory \
  --policy-fingerprint roleplay-memory-v1 \
  --required-role autobiography=action/roleplay.first-person-autobiography \
  --required-role world=observation/roleplay.world-understanding \
  --member autobiography=<artifact-id> \
  --member world=<artifact-id> \
  --expected-previous none \
  --report-json gitignore/reports/publish-set.json
```

`--expected-previous` 是强制 CAS：genesis 明确写 `none`，后续写 exact `das_...`
id。CLI 从 members 的唯一 common anchor 读取 raw-authoritative governing setup
address/schema/payload hash；setup refs 不能由参数伪造。发布只写
`derived/memory/v1/sets` 与 latest pointer；raw SessionJournal 不写任何 derived-set
definition/activation event。

### list-derived-artifact-sets

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  list-derived-artifact-sets \
  --input gitignore/session-journal/<session> \
  --report-json gitignore/reports/derived-inventory.json
```

inventory 严格验证每个 set/pointer 的 self identity 与 exact member artifact，
并按 exact key/id 稳定排序。它有意保留 missing/stale pointer、fork/cycle 供诊断；
这些 topology 问题不会让 list 失败。

### validate-derived-memory

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  validate-derived-memory \
  --input gitignore/session-journal/<session> \
  --report-json gitignore/reports/derived-validation.json
```

严格、只读验证所有 artifact/set/pointer，以及每个 exact key 的 canonical role
snapshot、完整无环单 tip lineage 和 `latest pointer == tip`。未被 set 使用的
orphan artifact 合法，便于 prompt tuning 保存 alternatives。空 derived repo
也合法。DM-5 起 validation report schema 是
`atelia.session-journal.cli.derived-memory-validation.v2`，新增 planner config/current
与 epoch/latest counts；这是 pre-release direct cutover，不输出旧 v1 shape。该命令不
rebuild、不创建目录或 lock。

### rebuild-derived-artifact-set-latest

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  rebuild-derived-artifact-set-latest \
  --input gitignore/session-journal/<session> \
  --lineage main \
  --coherence-group roleplay.default \
  --policy-id roleplay-memory \
  --policy-fingerprint roleplay-memory-v1 \
  --required-role autobiography=action/roleplay.first-person-autobiography \
  --required-role world=observation/roleplay.world-understanding
```

该命令只重建一个 exact lineage/coherence/policy/role-snapshot key 的 latest
pointer。没有 matching set、missing predecessor、role drift、fork 或 cycle
都会 fail fast，不会猜测 tie-break。

## Shared DerivedArtifactEpochPlanner 命令

这些命令只规划 shared history coverage；不会调用 LLM、运行 maintainer、发布
ArtifactSet 或写 raw event。

DM-5 v1 的 `--lineage` 只接受 `main`。参数仍显式进入 durable key/report，但在具备真正的
ref/lineage authority 前，不把任意 token 伪装成 branch-aware planning。

### configure-derived-artifact-planner

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  configure-derived-artifact-planner \
  --input gitignore/session-journal/<session> \
  --lineage main \
  --coherence-group roleplay.default \
  --topology-version roleplay-memory-v1 \
  --minimum-recent-tokens 24000 \
  --epoch-trigger-tokens 12000 \
  --scheduling-headroom-tokens 8000 \
  --hard-limit-tokens 64000 \
  --expected-current none
```

config 是 immutable lineage；更新时把 `--expected-current` 改为当前 `dpc_...` id。
相同 definition 的重试幂等，真实 cutover 只影响未来 epoch。
`hard-limit-tokens` 必须严格大于
`minimum-recent-tokens + epoch-trigger-tokens + scheduling-headroom-tokens`，
保证正常 trigger 在 backpressure 前可达。

### plan-derived-artifact-epoch

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  plan-derived-artifact-epoch \
  --input gitignore/session-journal/<session> \
  --lineage main \
  --coherence-group roleplay.default \
  --expected-previous none \
  --input-set none \
  --report-json gitignore/reports/plan-epoch.json
```

genesis 必须同时使用两个 `none`。后续规划必须同时给出 exact previous `dae_...`
与真实 input `das_...`；input set 必须属于同 lineage/coherence group，且 common
anchor 必须与 previous epoch 终点完全一致。每次调用最多发布一个 epoch；未达到
trigger 会报告 `BelowTrigger`，达到 hard limit 但没有合法 boundary 会显式
backpressure。

### list-derived-artifact-epochs

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  list-derived-artifact-epochs \
  --input gitignore/session-journal/<session> \
  --report-json gitignore/reports/epoch-inventory.json
```

报告稳定排序且 content-free，只包含 config/epoch identity、raw addresses、cost 与
read diagnostics，不包含 conversation 或 derived block 文本。

## llm-smoke

发送一次最小 Completion 请求，用于验证 connection/provider/call-log：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- llm-smoke \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --call-log-dir gitignore/session-journal/llm-smoke-calls
```
