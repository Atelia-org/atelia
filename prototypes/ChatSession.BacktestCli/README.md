# ChatSession.BacktestCli

`ChatSession.BacktestCli` 是离线检查和回放 ChatSession 历史的实验 CLI。它同时支持：

- 读取 `ChatSessionEngine` 导出的 legacy upgrade JSON，检查数据或运行无副作用 backtest。
- 把 legacy export 导入 `SessionJournal` raw repo。
- 从 `SessionJournal` addressed replay 运行 MemoryMaintainer，并把成功结果写成可重建的
  Derived Recap Artifact。

CLI 不启动真实聊天服务。当前 LLM maintainer 统一采用单次完整 Rewrite，不暴露工具、不运行
tool-loop。已归档的 Recording / Compression / two-stage Text Edit Agent 实验可通过 tag
`memory-maintainer-agentic-experiment-v1` 查阅；重构决策见
`docs/Galatea/memory-maintainer-slimming-refactor.md`。

## Legacy 输入命令

### inspect

检查 legacy export 的 schema、branch、事件数量和 message kind 分布。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- inspect \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json
```

### replay-pattern-count

运行不调用 LLM 的轻量规则分析器，用于验证 legacy replay cursor、阈值触发和 JSONL 输出。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-pattern-count \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 24000 \
  --output gitignore/backtest/pattern-count.jsonl \
  --report-md gitignore/backtest/pattern-count.md
```

参数：

- `--input <path>`：legacy export JSON。
- `--output <jsonl>`：逐 epoch 写入的 JSONL 结果。
- `--report-md <path>`：可选，写出最终 Markdown 摘要。
- `--threshold-tokens <n>`：估算 token 数达到阈值后才触发分析，默认 `24000`。
- `--respect-original-compaction`：按 export 中原始 compaction 事件回放；默认忽略。

### replay-rolling-summary

从 legacy export 运行 LLM maintainer backtest。它使用 synthetic sliding prefix，但不写
SessionJournal Derived Recap Artifact。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 24000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --output gitignore/backtest/legacy-world-understanding/result.jsonl \
  --call-log-dir gitignore/backtest/legacy-world-understanding/calls \
  --max-epochs 1 \
  --preset world-understanding-rewrite
```

## SessionJournal 工作流

### import-session-journal

把 legacy upgrade export 导入新的 `SessionJournal` repo。导入只写 raw facts：
`initial-state` 成为初始化 setup/session 链，observation/action 成为对应 raw events，
legacy `compaction` / `recap` 作为可重建 derived 信息跳过。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- import-session-journal \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --output gitignore/session-journal/cyber-copy-upgraded \
  --report-md gitignore/session-journal/cyber-copy-upgraded-import.md
```

参数：

- `--input <path>`：legacy export JSON。
- `--output <repo-dir>`：新 `SessionJournal` repo。目录已存在且非空时默认失败。
- `--force`：删除并重建整个目标 repo；只应用于已经确认可替换的输出路径。
- `--report-md <path>`：可选，写出 legacy ordinal 到新 `EventAddress` 的映射。

### replay-rolling-summary-session-journal

从 `SessionJournal` raw repo 的 addressed history replay 运行 maintainer。成功 completion 会先写入
`derived/recaps/v1/`，store 成功后 runner 才提交本 epoch 的 `MemoryPack` 和 sliding prefix。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary-session-journal \
  --input gitignore/session-journal/cyber-copy-upgraded \
  --threshold-tokens 24000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --output gitignore/backtest/session-journal-autobiographical/result.jsonl \
  --call-log-dir gitignore/backtest/session-journal-autobiographical/calls \
  --max-epochs 1 \
  --preset autobiographical-rewrite
```

两条 LLM replay 命令共享这些参数：

- `--input <path>`：legacy 命令接受 export JSON；SessionJournal 命令接受 repo 目录。
- `--output <jsonl>`：逐 epoch replay record。
- `--connections <path>` / `--connection <id>`：Completion connection 配置和可选连接 ID。
- `--call-log-dir <dir>`：每次 LLM 调用的请求/响应日志目录。
- `--threshold-tokens <n>`：达到阈值后触发 maintainer，默认 `24000`。
- `--max-epochs <n>`：本次最多产生多少个 epoch；真实 LLM 验收建议先用 `1`。
- `--preset <name>`：`autobiographical-rewrite` 或 `world-understanding-rewrite`。
- `--system-prompt <path>` / `--prompt <path>`：可选，覆盖 preset 的 system/user prompt。

对 SessionJournal 命令，`--output` 和 `--call-log-dir` 必须位于输入 repo 外。JSONL 先写到同目录
临时文件，replay 正常结束后才替换最终路径；preflight/configuration 失败不会截断已有报告。为避免
路径 alias 绕过 repo 边界，`--input`、`--output`、`--call-log-dir` 的路径链不能包含
symlink/reparse point。

当前可用 preset：

| preset | target | 用途 |
| --- | --- | --- |
| `autobiographical-rewrite` | `Action / roleplay.first-person-autobiography` | 将新经历融入第一人称自传并完整重写。 |
| `world-understanding-rewrite` | `Observation / roleplay.world-understanding` | 维护事实档案和世界认知地图。 |

输出 JSONL schema 为 `atelia.chat-session.memory-maintainer-backtest.v2`。除调用、target、split 和状态信息外，
SessionJournal 成功 record 还包含：

- `sourceRawHead`、`sourceStartInclusive`、`sourceEndInclusive`：本次 replay snapshot 与实际吸收范围。
- `artifactId`、`artifactPath`、`anchorRawEvent`、`previousArtifact`：实际落盘 artifact 的链接。
- `callLogPaths`：本 epoch 实际写出的 Completion call log。

### Full replay 与重新生成

当前 SessionJournal 命令是从 raw history 起点、空 `MemoryPack` 开始的 full replay。因此目标
`profile + target` lineage 必须为空；已有 artifact 时命令会在首次 LLM 调用前失败。它尚不支持从
latest artifact materialize 后只 replay anchor 之后的 tail。

两个正式 preset 的 profile/target lineage 不同，因此可以在同一个新 repo 上各运行一次；第二个
preset 的 root artifact 不会把第一个 preset 的 artifact 设为 `previousArtifact`。但同一 preset
不能在未清理其既有 lineage 时直接重复 full replay。

若要验证 derived artifact 可重建，只删除该 repo 的 derived recap store，保留 raw repo：

```bash
rm -rf gitignore/session-journal/cyber-copy-upgraded/derived/recaps/v1
```

然后用新的 `--output` / `--call-log-dir` 重新运行命令。不要用
`import-session-journal --force` 代替这一步；后者会删除并重建整个 repo。

## llm-smoke

发送一次最小 LLM 请求并写 call log，用于先检查 connection、API key 和 provider wrapper。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- llm-smoke \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection dsv4p \
  --call-log-dir gitignore/backtest/llm-smoke-calls \
  --message "请用一句话回复：LLM smoke test ok。"
```

## 模块职责

`ChatSession.BacktestCli` 是薄 CLI 壳，主要依赖：

- `ChatSession`：legacy event source DTO、读取和投影，以及共享 history split policy。
- `SessionJournal`：raw journal replay、address/provenance、`MemoryPack`、maintainer/orchestrator substrate
  和 `DerivedRecapStore`。
- `ChatSession.Memory`：`AutobiographicalRewriteProfiles`、
  `WorldUnderstandingRewriteProfiles` 等内容层 profile。
- `Completion`：connection loader/registry、真实 provider client 和 `LoggingCompletionClient`。

推荐先导入 repo，再用 `--max-epochs 1` 分别审阅 JSONL、artifact 和 call log；确认输出后再提高 epoch
数量。真实 LLM 输出和日志应写到 `gitignore/backtest/...`，避免把大体积或敏感上下文放入源码 diff。
