# Task 04e: ChatSession Semantic Event Source Replay Migration

> 状态：Todo
> 依赖：Task 01 / 04b / 04c / 04d / 06
> 目标输入：`prototypes/Galatea/.atelia/galatea/sessions/cyber-copy`

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

建议 importer 放在 `prototypes/ChatSession` 层，而不是 `prototypes/Galatea` 层。

理由：

- `MessageRecord` 是 ChatSession 内部 schema，ChatSession 层能直接构造 durable message records。
- repo-level migration 需要直接写 StateJournal root / messages / commit note，不应走 HTTP 或 Galatea UI 层。
- `ChatSessionEngine.SendMessageAsync` 和 `CompactAsync` 都会调用 LLM 或重新总结，不能用于迁移 replay。

建议 API：

```csharp
ChatSessionLegacyEventSourceImporter.Import(
    inputJsonPath,
    outputRepoDir,
    branchName: "main"
)
```

导入策略：

1. `Repository.Create(outputRepoDir)`。
2. `CreateBranch(branchName)`。
3. 创建 ChatSession root dict 与 messages deque。
4. 按 event 顺序重放 message delta。
5. 每个 event 后调用 `Repository.Commit(root, ChatSessionCommitMetadata.EncodeNote(kind, reason))`。
6. 对 compaction event 写入 recap 时必须带 `RecapSourceAnchor`。
7. 最终生成一个可被 `ChatSessionEngine.OpenAsync` / `ChatSessionHistoryReader.ReadCurrent` 打开的新版 repo。

建议输出目录：

```text
prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded
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
