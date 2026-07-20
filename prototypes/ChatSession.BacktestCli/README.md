# ChatSession.BacktestCli

`ChatSession.BacktestCli` 是用于离线检查和回放 `ChatSession` legacy export 的实验 CLI。它的主要用途不是启动真实聊天服务，而是在可重复输入上验证 memory substrate、memory block maintainer、prompt preset 和 Completion connection 的行为。

当前最常用的输入是 `ChatSessionEngine` 导出的 legacy upgrade JSON，例如：

```bash
prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json
```

CLI 会按 export 中的事件顺序重放历史，并在满足 token 阈值时触发分析或 LLM maintainer。`replay-rolling-summary` 使用的是 synthetic sliding prefix：它忽略原始 compaction 事件，把当前活跃历史中即将滑出窗口的前缀作为 `RecentHistorySlice` 交给 maintainer。

## 命令

### inspect

检查 legacy export 的 schema、branch、事件数量和 message kind 分布。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- inspect \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json
```

### replay-pattern-count

运行一个不调用 LLM 的轻量规则分析器，用于验证 replay cursor、阈值触发和 JSONL 输出链路。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-pattern-count \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 12000 \
  --output gitignore/backtest/pattern-count.jsonl \
  --report-md gitignore/backtest/pattern-count.md
```

常用参数：

- `--input <path>`：legacy export JSON。
- `--output <jsonl>`：逐 epoch 写入的 JSONL 结果。
- `--report-md <path>`：可选，写出最终 Markdown 摘要。
- `--threshold-tokens <n>`：估算 token 数达到阈值后才触发分析，默认 `12000`。
- `--respect-original-compaction`：按 export 中原始 compaction 事件回放；默认忽略原始 compaction。

### llm-smoke

用指定 Completion connection 发送一次最小 LLM 请求，并写出 call log。这个命令用于先确认连接配置、API key、provider wrapper 和日志目录是否正常。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- llm-smoke \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --call-log-dir gitignore/backtest/llm-smoke-calls \
  --message "请用一句话回复：LLM smoke test ok。"
```

常用参数：

- `--connections <path>`：Completion connection 配置文件。
- `--connection <id>`：可选，指定 connection；不传时使用配置中的默认连接。
- `--call-log-dir <dir>`：call log 输出目录，默认 `gitignore/backtest/llm-smoke-calls`。
- `--message <text>`：发送给 smoke-test assistant 的 observation 文本。

### replay-rolling-summary

用真实 LLM maintainer 回放 memory block 更新。命令名保留了最早的 rolling summary 实验语义，但现在已经扩展为 memory maintainer preset 调试台。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 24000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --output gitignore/backtest/content/world-understanding.jsonl \
  --call-log-dir gitignore/backtest/content/world-understanding-calls \
  --max-epochs 1 \
  --preset world-understanding
```

常用参数：

- `--input <path>`：legacy export JSON。
- `--output <jsonl>`：逐 epoch 写入的回放结果。
- `--connections <path>`：Completion connection 配置文件。
- `--connection <id>`：可选，指定 connection；不传时使用默认连接。
- `--call-log-dir <dir>`：每次 LLM 调用的原始请求/响应日志目录。
- `--threshold-tokens <n>`：当前活跃历史估算 token 数达到阈值后触发 maintainer，默认 `12000`。
- `--max-epochs <n>`：最多触发多少次 maintainer，做真实 LLM 实验时建议先用 `1`。
- `--preset <name>`：选择 maintainer preset。
- `--system-prompt <path>`：覆盖 preset 的 system prompt。
- `--prompt <path>`：覆盖 preset 的 user prompt。
- `--target-carrier system|observation|action`：仅 `rolling-summary` preset 使用，默认 `observation`。
- `--target-block <id>`：仅 `rolling-summary` preset 使用，默认 `session.rolling-summary`。

可用 preset：

| preset | target | 用途 |
| --- | --- | --- |
| `rolling-summary` | 默认 `Observation / session.rolling-summary`，可用参数覆盖 | 通用滚动摘要，保留长期有用事实、决策、路径、验证结果和待办。 |
| `world-understanding` | `Observation / roleplay.world-understanding` | 维护 role-play 角色可看到的外层知识、世界理解和事实档案。 |
| `first-person-autobiography` | `Action / roleplay.first-person-autobiography` | 维护 Galatea 第一人称自传、自我连续性材料和关键原话。 |

输出 JSONL 每行代表一次 maintainer epoch，包含 `presetName`、`eventOrdinal`、`thresholdTokens`、`splitIndex`、`slidingOutMessageCount`、`targetCarrier`、`targetBlockId`、新旧 block 预览、call log 路径、状态和错误信息。

## 与引用项目的关系

`ChatSession.BacktestCli` 是一个薄 CLI 壳，核心能力来自三个项目引用：

- `../ChatSession/ChatSession.csproj`
  - 提供 legacy event source 读取、replay cursor、history message projection、`MemoryPack`、`RecentHistorySlice`、`IMemoryBlockMaintainer` 和 `CompletionMemoryBlockMaintainer` 等 memory substrate 基础设施。
- `../ChatSession.Memory/ChatSession.Memory.csproj`
  - 提供可复用内容层 preset，包括 `RolePlayMemoryBlockPaths`、`WorldUnderstandingMemoryMaintainer`、`FirstPersonAutobiographyMemoryMaintainer` 和 prompt 常量。
  - CLI 的 `world-understanding` / `first-person-autobiography` preset 直接实例化这些 maintainer，用于在真实 export 上调试内容层效果。
- `../Completion/Completion.csproj`
  - 提供 `CompletionConnectionConfigLoader`、`CompletionConnectionRegistry`、`DefaultCompletionClientFactory` 和 `LoggingCompletionClient`。
  - `llm-smoke` 与 `replay-rolling-summary` 都通过这里读取 connection、调用真实 LLM，并写出 call log。

因此，这个 CLI 适合承担三类工作：

- 数据面检查：确认 legacy export 的事件和 message 分布是否符合预期。
- substrate 回归：不接 LLM 时验证 replay、token 阈值、JSONL/report 输出是否稳定。
- 内容层实验：接真实 LLM，对不同 memory maintainer prompt/preset 做小步回测和结果审阅。

## 推荐工作流

1. 先用 `inspect` 确认输入文件可读、事件数量合理。
2. 用 `llm-smoke` 验证 connection 和 call log。
3. 用 `replay-rolling-summary --max-epochs 1` 跑单轮，检查 JSONL 的 target、状态和 `newBlock.tailPreview`。
4. 审阅 call log 中的 prompt、上下文投影和模型输出。
5. 再逐步提高 `--max-epochs` 或调整 `--threshold-tokens`。

真实 LLM 实验建议把 `--output` 和 `--call-log-dir` 写到 `gitignore/backtest/...`，避免把大体积日志和敏感请求内容放进常规源码 diff。
