# ChatSession Memory Backtest CLI 设计备忘

> 状态：设计备忘。用途是给后续 coding agent 会话快速恢复“离线 replay / memory maintainer 回测 / CLI 工具化”的总体思路。

## 1. 背景

Galatea Memory Substrate 第一阶段已经在 `Atelia.ChatSession` 中落地：

- `MemoryPack` / `MemoryPackDraft` / `RenderedMemoryPack`
- `RecentHistorySlice` / `ContextHeaderSnapshot`
- `IMemoryBlockMaintainer`
- `CompletionMemoryBlockMaintainer`
- `MemoryMaintenanceOrchestrator`

下一步需要一个离线回测框架，用已有聊天历史数据反复验证 analyzer / maintainer 是否顺手，尤其用于：

- 调试 memory maintainer 的输入窗口与输出格式。
- 调试 LLM prompt，不污染真实 session。
- 比较不同 rolling summary / MemoryPack 策略。
- 生成可审计的 JSONL / Markdown 报告。

当前可用样例数据：

```text
prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json
```

该文件由 `ChatSessionLegacyUpgradeExporter` 导出，schema 为：

```text
atelia.chat-session.legacy-upgrade-export.v1
```

已抽样确认：样例中有 77 个 events，其中 71 个 `model-turn`、2 个 `compaction`、3 个 `update-system-prompt`、1 个 `initial-state`；消息层约为 71 条 observation + 71 条 action。

## 2. 已有可复用基础

`prototypes/ChatSession/ChatSessionLegacyEventSourceImporter.cs` 已经实现了一套 event source importer：

- 读取 `chat-session-legacy-upgrade-export.json`。
- 校验 schema。
- 按 event 顺序 replay。
- 把 event source 导入为新的 StateJournal ChatSession repo。
- 内部已有 `EventDto` / `MessageDto` / `ToHistoryMessage(...)` 等解析逻辑。

Backtest CLI 不应重新手写一套不一致的 JSON schema 解析。推荐先把 importer 中的私有读取 / DTO / message projection 逻辑抽成可复用 reader：

```csharp
ChatSessionLegacyEventSourceReader.Read(path)
ChatSessionLegacyReplayCursor.Step()
```

Importer 和 Backtest CLI 共用同一套 reader / replay cursor。

## 3. CLI 定位

建议新建项目：

```text
prototypes/ChatSession.BacktestCli
```

理由：

- 输入格式属于 ChatSession legacy upgrade export。
- replay cursor 属于 ChatSession substrate。
- `MemoryPack` / `IMemoryBlockMaintainer` 也在 `Atelia.ChatSession`。
- Galatea 后续只提供具体 key、maintainer 策略、prompt 和 UI 行为。

CLI 第一阶段不需要写 StateJournal repo。直接 in-memory replay event source 就足以验证 maintainer / MemoryPack 行为。

## 4. 推荐数据流

```text
legacy export json
→ event stream
→ in-memory replay state: root metadata + current history
→ backtest policy decides when to run memory maintenance
→ RecentHistorySlice + MemoryPack snapshot
→ IMemoryBlockMaintainer[]
→ MemoryPack updated through MemoryMaintenanceOrchestrator
→ JSONL / Markdown report
```

Backtest report 应至少记录：

- event ordinal / commit / event kind
- 当前 history message count
- estimated tokens
- 是否触发 maintenance epoch
- split index / recent window 范围
- maintainer id / target path
- old block text
- new block text
- diagnostics / notices / errors / tool call count

## 5. 第一阶段任务：确定性 Maintainer 回测

第一阶段不要直接上 LLM rolling summary。先做一个确定性 maintainer，避免把框架问题和 prompt / provider 问题搅在一起。

推荐测试任务：维护“Galatea 迄今为止说过多少次 `不是……而是……` 句式”。

目标 block：

```text
MemoryPackCarrier.Action / galatea.pattern.not-but-count
```

维护逻辑：

- 扫描 recent history 中的 `ActionMessage` 文本。
- 统计 `不是...而是...` / `不是……而是……` 风格句式。
- 读取 old block 中上一轮累计状态。
- 输出新版累计状态。

输出 block 可先用简单 Markdown：

```markdown
totalCount: 12

latestExamples:
- event 10: 不是 X，而是 Y
- event 21: 不是 A，而是 B
```

这一任务能验证：

- event source replay 顺序是否正确。
- recent history window 是否合理。
- target old block 不存在时空 block 创建是否自然。
- `MemoryPackDraft` 是否稳定保序。
- maintainer 输出是否易审计。
- CLI 报告是否足够调试。

## 6. 第二阶段任务：复现 CompactAsync 风格 Rolling Summary

`CompactAsync` 适合作第二阶段任务。它引入 LLM summarizer、压缩质量评估、prompt 调试，变量更多，因此不建议作为第一阶段。

第二阶段已经明确不只是“加一个命令”，还需要补一块可复用基础设施：从统一的 connection 配置构造真实 `ICompletionClient`，并对每次 LLM 调用保存完整输入 / 输出 / 元数据日志。这样 backtest 才能成为 prompt tuning 的工作台，而不是一次性脚本。

配套设计见：[`llm-connection-and-call-log-plan.md`](llm-connection-and-call-log-plan.md)。

回测时应使用“只取 model turns，自己按阈值触发 rolling summary”的方式，不依赖导出文件中已有的真实 `compaction` 事件。

也就是说，rolling summary 回测模式应忽略原始 `compaction` event 对 history 的缩短效果，用 model-turn 序列模拟：

```text
replay model-turn events one by one
if estimatedTokens >= threshold:
    split = FindHalfContextSplitPoint(history)
    slice = old summary + history[split..]
    maintainer updates summary block
    report epoch
```

推荐将 rolling summary 也表达成 MemoryPack block：

```text
MemoryPackCarrier.System / session.rolling-summary
```

或：

```text
MemoryPackCarrier.Observation / session.rolling-summary
```

具体放哪个 carrier 是策略问题，不应写死在 substrate。

输入：

```text
OldBlock: 上一版 rolling summary，初始为空 block
PriorContext: 旧 ContextHeader / 旧 MemoryPack render
RecentHistory: split 后的后半窗口
```

输出：

```text
NewBlock: 新 rolling summary
```

推荐第一版 `replay-rolling-summary` 专门实现一个 `RollingSummaryMaintainer`，而不是立刻强行复用 `CompletionMemoryBlockMaintainer`。原因是 rolling summary backtest 需要更多调试信息：prompt 文件快照、window dump、summary diff、call log 路径、异常状态、耗时等。等这些字段稳定后，再判断哪些能力应回收进通用 maintainer substrate。

第一版命令建议：

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 12000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection local-deepseek \
  --system-prompt prompts/summary-system.md \
  --prompt prompts/summary-user.md \
  --output gitignore/backtest/rolling-summary.jsonl \
  --call-log-dir gitignore/backtest/rolling-summary-calls
```

JSONL report 每个 epoch 至少记录：

- epoch index
- triggering event ordinal / commit
- replay mode
- threshold tokens / estimated tokens
- split index / old-window count / recent-window count
- target carrier / block id
- old summary block length + tail preview
- new summary block length + tail preview
- call log file path
- status / exception summary

LLM call log 文件保存完整 request / response；JSONL report 只保存索引和摘要，避免主 report 被大 prompt 淹没。

## 7. Original Compaction Event 的处理模式

导出文件中可能已有真实 `compaction` 事件。Backtest CLI 应支持至少两种模式：

### 7.1 ignore-original-compaction

只 replay `initial-state`、`model-turn`、`update-system-prompt` 等能构造线性对话历史的事件；忽略原始 `compaction` 对 history 的缩短效果。

用途：

- 回测“如果新 memory policy 当时运行，会发生什么”。
- 复现 `CompactAsync` 风格 rolling summary。

这是 rolling summary 回测的推荐默认模式。

### 7.2 respect-original-compaction

按 event source 原样 replay，包括真实 `compaction` event。

用途：

- 验证 importer / replay fidelity。
- 测试 maintainer 如何跟随真实历史变化。

## 8. CLI 命令草案

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- inspect \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json
```

输出 event / message 统计、schema、branch、compaction 分布。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-pattern-count \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 12000 \
  --output gitignore/backtest/not-but-count.jsonl
```

运行确定性 `不是……而是……` maintainer。

```bash
dotnet run --project prototypes/ChatSession.BacktestCli -- replay-rolling-summary \
  --input prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json \
  --threshold-tokens 12000 \
  --connections prototypes/Galatea/.atelia/galatea/connections.json \
  --connection local-deepseek \
  --system-prompt prompts/summary-system.md \
  --prompt prompts/summary-user.md \
  --output gitignore/backtest/rolling-summary.jsonl \
  --call-log-dir gitignore/backtest/rolling-summary-calls
```

后续可加：

- `--until-event`
- `--from-event`
- `--dump-window`
- `--dump-memory-pack`
- `--dry-run`
- `--respect-original-compaction true|false`

## 9. 最小施工切片

建议第一轮实现只做：

1. 抽出 legacy export reader / replay cursor。
2. 新建 `prototypes/ChatSession.BacktestCli`。
3. 实现 `inspect`。
4. 实现确定性 `replay-pattern-count` maintainer。
5. 输出 JSONL 和简短 Markdown report。
6. 不接 LLM，不做 rolling summary。

完成后再实现第二轮：

1. 在 `Atelia.Completion` 中抽出公共 connection config / factory / registry。
2. 在 `Atelia.Completion` 中实现 logging completion client，每次 LLM 调用落盘。
3. `ChatSession.BacktestCli` 增加 `--connections` / `--connection` / `--call-log-dir`。
4. 实现 `replay-rolling-summary`，默认 `ignore-original-compaction`。
5. 支持 prompt 文件，并在 call log 中保存 prompt 文件路径、内容快照或 hash。
6. 生成 summary diff / token estimate / window dump / call log path。
7. 先用专用 `RollingSummaryMaintainer` 跑通，再评估是否回收进 `CompletionMemoryBlockMaintainer`。

## 10. 开放问题

- `ChatSessionLegacyEventSourceReader` 应放在 `Atelia.ChatSession` public API 还是 internal + `InternalsVisibleTo` 给 CLI？倾向 public，因 CLI 是正常消费者。
- Backtest report schema 是否要稳定成 `atelia.chat-session.memory-backtest-report.v1`？倾向从 JSONL 开始，等第二轮再固定 schema。
- token threshold 使用现有 `ChatSessionTokenEstimator` 即可，但它当前是 internal；如果 CLI 需要直接使用，应考虑暴露一个 public estimate helper 或由 ChatSession 提供 replay policy。
- 确定性 pattern maintainer 的 block 输出先用 Markdown，还是 JSON？倾向 Markdown，便于 LLM 维护器后续接手；JSONL report 负责机器可读审计。
- connection config 应放在哪层？倾向 `Atelia.Completion`，不是 `Atelia.ChatSession`。ChatSession 继续只接受 `ICompletionClient`，Completion 提供官方 config-to-client 路径。
- Galatea / FamilyChat 现有 connection registry 是否立即迁移？倾向先让 Backtest CLI 使用公共实现；应用层后续小步迁移，避免第二阶段一次改太宽。
- LLM call log 是每次调用一个文件，还是 request / response / meta 分文件？倾向每次调用一个完整 JSON 文件；JSONL report 只引用该文件路径。
