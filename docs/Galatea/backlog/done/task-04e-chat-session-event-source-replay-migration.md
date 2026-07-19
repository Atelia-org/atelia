# Task 04e: ChatSession Semantic Event Source Replay Migration

> 状态：Done
> 依赖：Task 01 / 04b / 04c / 04d / 06
> 目标输入：`prototypes/Galatea/.atelia/galatea/sessions/cyber-copy`

## 阶段进展

- 04e-a 已完成：`ChatSessionLegacyUpgradeExporter` 已扩展 `events` 字段，可导出 replay 所需的 semantic delta event source。
- 04e-b 已完成：实现 importer / replayer，把 event source 导入为新的 StateJournal repo，并在 `cyber-copy` 上生成 `cyber-copy-upgraded`。

04e-a 在 `cyber-copy` 上的实跑摘要：

```text
timeline=77
events=77
recapMappings=2
warnings=0
Compaction:2,InitialState:1,ModelTurn:71,UpdateSystemPrompt:3
compactionEvents=2
```

04e-b 在 `cyber-copy` 上的实跑摘要：

```text
importEvents=77
oldMessages=77
newMessages=77
oldTimeline=77
newTimeline=77
newExplicitMetadata=77
newWarnings=0
newCurrentRecapAnchors=1
oldKinds=Compaction:2,InitialState:1,ModelTurn:71,UpdateSystemPrompt:3
newKinds=Compaction:2,InitialState:1,ModelTurn:71,UpdateSystemPrompt:3
mismatches=0
upgradedRepo=prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded
upgradedExport=prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json
```

## 背景

Task 04d 已能从旧 Galatea / ChatSession repo 导出稳定、可审阅的新版语义 JSON：它包含 commit timeline metadata、legacy recap source mapping 与 warnings。但 04d 的 JSON 仍不是可 replay 的事件源，因为它不包含足够的 message content delta，无法独立重建一个新的 StateJournal repo。

本任务记录下一阶段迁移方案：把 04d export 扩展为 reconstructed semantic event source，再用该 event source 生成一个新的 ChatSession StateJournal repo。目标不是做长期通用迁移框架，而是务实升级 `cyber-copy` 这一个旧 repo。

## 核心判断

这个 event source 不是原始事件流，而是 reconstructed semantic event source：

- commit 顺序来自 StateJournal effective parent-chain。
- `commitKind` / `commitReason` 对旧数据来自 recovery attribution，不是旧 reflog 的原生 note。
- recap source anchor 来自 compaction finding，是 best-effort 推断。
- message content 来自历史 commit snapshot 的 durable records。

因此导出和导入都必须保留 source / confidence / reason，不能把 legacy-inferred 语义伪装成旧数据原生事实。

## 阶段 1：扩展 04d JSON 为 Event Source

在 `ChatSessionLegacyUpgradeExporter` 中增加 `events` 字段，按 chronological effective timeline 排序。不要使用 raw reflog order；raw reflog 可能包含旁支、重复候选或回退点，不适合 replay。

状态：Done。

建议事件类型：

| event kind | 含义 | 关键字段 |
|---|---|---|
| `initial-state` | 创建 ChatSession root | root metadata、system prompt、空 messages |
| `model-turn` | 普通模型轮次 | appended messages |
| `compaction` | prefix summary compaction | source range、source messages、recap message、recap source anchor、confidence |
| `update-system-prompt` | root system prompt 更新 | old/new system prompt，或至少 new system prompt |
| `update-context-header` | context header 更新 | old/new header 或 resulting header |
| `revert-turn` | 移除最近完整 turn | removed suffix messages |
| `redundant-save` | 状态未变化的提交 | reason、metadata |
| `other` | 无法分类提交 | before/after summary、reason、warnings |

### Message DTO

事件中的 message content 应保留 ChatSession message-level 结构，而不是只保留 flattened text。

建议 DTO：

- `kind`: `observation` / `action` / `tool-results` / `recap` / `context-header`
- `content`: observation / recap 的文本内容
- `timestampUtc`: 若原 durable record 有 timestamp，则保留
- `action.blocks`: action block DTO，至少支持 `text` 与 `tool-call`
- `toolResults.results`: tool name / call id / status / content
- `contextHeader`: system prompt fragment / user message / assistant message
- `recapSourceAnchor`: 对 recap 可选

第一版可以重点覆盖 `cyber-copy` 实际出现的 observation / action / recap；但 schema 应预留 tool-results 与 context-header，避免下一步马上破坏格式。

### Compaction Event

compaction 不能表达成“append recap”。它是序列变换：

```text
旧序列: prefix + suffix
新序列: recap + suffix
```

若存在 protected context header，则可能是：

```text
旧序列: headers + prefix + suffix
新序列: headers + recap + suffix
```

因此 compaction event 必须包含：

- `oldHead`
- `newHead`
- `sourceRange`
- `sourceMessages`: 从 `oldHead` 的 historical root 中读取 `[sourceStartIndex, sourceEndExclusive)`
- `recapIndex`
- `recapMessage`: 从 `newHead` 的 recap index 读取
- `recapSourceAnchor`: 按 Task 01 字段生成
- `suffixMatchCount`
- `confidence`
- `reason`

## 阶段 2：导入 Event Source 为新 StateJournal Repo

状态：Done。

### 实施条件判断

04e-b 已具备实施条件。04e-a 生成的 `chat-session-legacy-upgrade-export.json` 已满足 importer 的核心输入要求：

- `events.Count == timeline.Count`。
- `events` 按 effective parent-chain 的 chronological order 排列。
- `initial-state` event 含 root metadata 与初始 messages。
- `model-turn` event 含 `appendedMessages`。
- `compaction` event 含 `sourceRange`、`sourceMessages`、`recapMessage`、`recapSourceAnchor`、`confidence`。
- `update-system-prompt` event 含 `systemPromptChange`。
- `cyber-copy` 实际 event kinds 只有 `initial-state` / `model-turn` / `compaction` / `update-system-prompt`，04e-b 第一版 importer 已支持这些分支；`revert-turn`、`update-context-header`、`tool-results` 的复杂实跑分支留待出现真实样本后再硬化。

已知输入基线：

```text
events=77
eventKinds=initial-state,model-turn,compaction,update-system-prompt
compactionEvents=2
warnings=0
```

### 推荐实施边界

建议 importer 放在 `prototypes/ChatSession` 层，而不是 `prototypes/Galatea` 层。

理由：

- `MessageRecord` 是 ChatSession 内部 schema，ChatSession 层能直接构造 durable message records。
- repo-level migration 需要直接写 StateJournal root / messages / commit note，不应走 HTTP 或 Galatea UI 层。
- `ChatSessionEngine.SendMessageAsync` 和 `CompactAsync` 都会调用 LLM 或重新总结，不能用于迁移 replay。

本阶段建议新增：

- `ChatSessionLegacyEventSourceImporter`
- focused importer tests
- 一个临时 runner 或测试辅助，用于生成 `cyber-copy-upgraded`

状态：Done。

暂不建议新增 Galatea UI / HTTP API。

### DTO 与解析策略

当前 `ChatSessionLegacyUpgradeExporter` 的 JSON DTO 是 private nested record。04e-b 实施时有两种选择：

1. 把 export DTO 提升为 ChatSession 内部共享 DTO，例如 `ChatSessionLegacyUpgradeExportDto` / `ChatSessionLegacyEventDto`。
2. importer 直接用 importer-local DTO 读取 event source。

第一版实际选择 2 的局部变体：`ChatSessionLegacyEventSourceImporter` 定义 importer-local DTO，与当前 JSON schema 对齐。理由是当前目标聚焦 `cyber-copy` 单次迁移，先闭合 replay 与验证；若后续决定长期保留迁移工具，再把 exporter/importer DTO 抽成 internal shared DTO。

若后续转向长期工具，仍建议选择 1。理由：

- importer 和 exporter 可以共享 schema 形态，减少字段拼写漂移。
- focused tests 可以直接构造 DTO 或 roundtrip JSON。
- 后续如果决定保留迁移工具，DTO 不需要再拆第二次。

仍要保持 DTO 为 `internal` 或私有实现细节；不把它升级成长期 public API。

### MessageRecord 写入能力

Importer 需要按 event source 中的 message DTO 重建 durable message records。当前 `MessageRecord` 只有这些 public/internal 写入入口：

- `AppendObservation`
- `AppendAction`
- `AppendToolResults`
- `PrependRecap`
- `PrependContextHeader`

这不足以按任意顺序重建完整 message list，因为 recap / context-header 只有 prepend 入口。04e-b 应先在 `MessageRecord` 内补一个小的内部写入 helper，例如：

```csharp
internal static DurableDict<string> AppendHistoryMessage(
  DurableDeque messages,
  IHistoryMessage message,
  DateTimeOffset? timestampUtc = null
)
```

或至少补：

- `AppendRecap(...)`
- `AppendContextHeader(...)`

如果实现成本不高，建议让这些 helper 支持可选 `timestampUtc`。迁移验收不要求 timestamp 完全一致，但 event source 已保留 timestamp，保留下来更利于审计与 diff。

### Replay 状态模型

Importer 不应调用 `ChatSessionEngine.SendMessageAsync` 或 `CompactAsync`，而应维护一个内存态 replay state：

```text
root metadata
current systemPrompt
current message DTO list
```

每个 event 的处理方式：

| event kind | replay 行为 |
|---|---|
| `initial-state` | 创建 root 与 messages deque，写入 root metadata 和 event messages，然后 commit `initial-state` |
| `model-turn` | 将 `appendedMessages` append 到 current message list，重写 durable messages，commit `model-turn` |
| `compaction` | 按 `sourceRange` 从 current message list 删除 source range，再插入 protected prefix + `recapMessage`，确保 recap 带 `recapSourceAnchor`，commit `compaction` |
| `update-system-prompt` | 更新 root `systemPrompt`，commit `update-system-prompt` |
| `update-context-header` | 第一版可先 fail-fast，除非 event source 出现该 kind；后续按 resulting header 重建 |
| `revert-turn` | 第一版可按 `removedMessages.Count` 从尾部移除，并校验内容；`cyber-copy` 不需要 |
| `redundant-save` | 不改变 state，仅 commit 对应 metadata |
| `other` | 第一版 fail-fast，避免生成无法解释的新 repo |

### DurableDeque 重建策略

`DurableDeque` 只有 front/back push/pop/set，不适合任意 index insert。为了降低第一版复杂度，建议每个 event 先更新内存态 `current message DTO list`，然后把 durable deque 重写为该 list：

1. `PopBack` 或 `PopFront` 清空当前 durable deque。
2. 按 list 顺序 `AppendHistoryMessage(...)`。
3. commit。

这会让每个 commit 的底层 diff 比最小 mutation 大，但 `cyber-copy` 只有 77 个 commits，离线迁移可接受。优点是 compaction / revert / context-header 不需要依赖 deque 任意插入能力，生成的每个 commit snapshot 也更容易与 event state 对齐。

如果后续发现导出体积或性能不可接受，再优化为增量 mutation。

建议 API：

```csharp
ChatSessionLegacyEventSourceImporter.Import(
    inputJsonPath,
    outputRepoDir,
    branchName: "main"
)
```

建议第一版如果 `outputRepoDir` 已存在则 fail-fast，不自动删除。实跑前由人工或 runner 明确删除旧的 `cyber-copy-upgraded`，避免误删真实会话。

导入策略：

1. `Repository.Create(outputRepoDir)`。
2. `CreateBranch(branchName)`。
3. 创建 ChatSession root dict 与 messages deque。
4. 按 event 顺序重放 message delta。
5. 每个 event 后调用 `Repository.Commit(root, ChatSessionCommitMetadata.EncodeNote(kind, reason))`。
6. 对 compaction event 写入 recap 时必须带 `RecapSourceAnchor`。
7. 最终生成一个可被 `ChatSessionEngine.OpenAsync` / `ChatSessionHistoryReader.ReadCurrent` 打开的新版 repo。

其中 commit note 必须使用 explicit metadata：

```csharp
ChatSessionCommitMetadata.EncodeNote(kind, reason)
```

不要把 event source 中的 `metadataSource=legacy-inferred` 写进新 repo note；那只是迁移输入来源。新 repo 是 replay 后的新数据，commit note 应为 explicit。

建议输出目录：

```text
prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded
```

### 验证方案

推荐实现一个 focused verification helper，对比 old/current 与 upgraded/current：

1. 用 `ChatSessionHistoryReader.ReadCurrent(oldRepo)` 读取旧 repo 当前 HEAD。
2. 用 `ChatSessionHistoryReader.ReadCurrent(upgradedRepo)` 读取新 repo 当前 HEAD。
3. 比较 message count。
4. 比较 message kind 序列。
5. 比较 observation / action / recap 的核心文本：
  - observation: `Content`
  - action: `GetFlattenedText()` 与 action blocks JSON 等价结构
  - recap: `Content`
6. 检查 upgraded repo 中 recap 的 `SourceAnchor` 不为空，并匹配 event source 中的 `recapSourceAnchor`。
7. 用 `ChatSessionLegacyRecapRecovery.Analyze(upgradedRepo)` 检查新 repo timeline：
  - warnings 为 0。
  - commit attribution source 应为 `explicit`。
  - commit kind 分布应与 event source 一致。
8. 确认 output repo 可由 `ChatSessionEngine.OpenAsync(...)` 打开。

`cyber-copy` 目标验收基线与实跑结果：

```text
oldCurrentMessageCount == upgradedCurrentMessageCount
oldCurrentKinds == upgradedCurrentKinds
upgradedWarnings == 0
upgradedTimeline.Count == 77
upgradedCompactionCommits == 2
upgradedExplicitMetadataCount == 77
upgradedCurrentRecapWithSourceAnchorCount == upgradedCurrentRecapCount
```

## 验收

- 扩展后的 `chat-session-legacy-upgrade-export.json` 包含 `events`，并且 `events.Count == timeline.Count`。
- 所有 events 按 effective timeline chronological order 排列。
- `model-turn` event 可还原 appended observation/action messages。
- `compaction` event 包含 `sourceMessages`、`recapMessage`、`recapSourceAnchor`。
- event source 不包含绝对 repo 路径，不默认输出 volatile timestamp。
- importer 能从 event source 生成 `cyber-copy-upgraded` repo。
- 新 repo 可由 ChatSession 读取，当前 HEAD message kind/content 与旧 repo 当前 HEAD 等价。
- 新 repo 的 recap message 含 Task 01 source anchor。
- 新 repo 的 effective timeline commit metadata 是 explicit `commitKind` / `commitReason` note，而不是 legacy-inferred。
- 针对 `cyber-copy`，迁移后至少确认：
  - old/new current HEAD message count 一致。
  - old/new current HEAD message kind 序列一致。
  - observation / action / recap 核心文本一致。
  - recovery warnings 为 0 或有明确说明。

## 非目标

- 不做面向任意旧 repo 的通用迁移框架。
- 不要求保留旧 commit address；新 repo 是重新 replay 得到的新 commit graph。
- 不要求复刻旧 reflog 的 raw order 或旁支候选。
- 不通过 LLM 重新生成 action 或 recap 内容。
- 不在 Galatea UI 中暴露迁移入口；第一版可以用测试、临时 runner 或小型内部 API 驱动。

## 风险与注意事项

- action message 若只保存 flattened text，会损失 tool-call block 结构；event source 应尽量输出 block DTO。
- `cyber-copy` 当前可能没有 tool results，但 schema 不应因此排除 tool-results。
- compaction source range 的索引基于 compaction 前完整 context，包括可能存在的 context header。
- importer 直接写 `MessageRecord` 时要复用现有 schema helper，避免生成 ChatSession 读不回来的 durable record。
- legacy-inferred metadata 应只作为迁移输入；新 repo commit note 应写 explicit metadata。

## 建议实施顺序

1. 扩展 `ChatSessionLegacyUpgradeExporter`，输出 events 与 message DTO。
2. 为 `cyber-copy` 重新生成 event source JSON，人工抽查 compaction event。
3. 增加 event source exporter focused tests。
4. 实现 `ChatSessionLegacyEventSourceImporter`。
5. 生成 `cyber-copy-upgraded` repo。
6. 增加 old/current vs upgraded/current 的对比测试或验证命令。
7. 再决定是否需要把 importer 保留为长期工具，或只作为本次迁移用内部代码。
