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

## checkpoint-artifact-set

在至少两个 exact artifacts 具有 common anchor 后提交一次 activation：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  checkpoint-artifact-set \
  --input gitignore/session-journal/<session> \
  --member autobiography=<artifact-id> \
  --member world-understanding=<artifact-id>
```

命令先验证 repo，成功时只追加一条 `ArtifactSetCommitted`。

## llm-smoke

发送一次最小 Completion 请求，用于验证 connection/provider/call-log：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- llm-smoke \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --call-log-dir gitignore/session-journal/llm-smoke-calls
```
