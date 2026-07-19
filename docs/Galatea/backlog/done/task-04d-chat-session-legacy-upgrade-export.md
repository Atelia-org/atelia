# Task 04d: ChatSession Legacy Upgrade Export

> 状态：Done
> 依赖：Task 01 / 04b / 04c / 06

## 实施结果

- 新增 `ChatSessionLegacyUpgradeExporter`，输出 schema `atelia.chat-session.legacy-upgrade-export.v1`。
- 导出内容包含 `timeline`、`recapMappings`、`warnings`。
- `timeline[].commitMetadata` 输出 `commitKind`、`commitReason`、`metadataSource`。
- `recapMappings[].recapSourceAnchor` 对齐 Task 01 的 `RecapSourceAnchor` 字段；只有 resolved mapping 才输出 anchor，unresolved mapping 保留推断状态。
- `ChatSessionCommitAttribution` 增加 `Source`，用于区分 `explicit` 与 `legacy-inferred`。
- 已在 `prototypes/Galatea/.atelia/galatea/sessions/cyber-copy` 上实际导出 `chat-session-legacy-upgrade-export.json`。

实跑摘要：

```text
timeline=77
recapMappings=2
warnings=0
Compaction:2,InitialState:1,ModelTurn:71,UpdateSystemPrompt:3
```

## 背景

旧 Galatea / ChatSession repo 中的 recap 缺少 Task 01 新增的 source anchor，commit 也缺少 Task 06 的 explicit `commitKind` / `commitReason` note。Task 04b / 04c 已能只读推断 compaction source range 与 legacy commit attribution；本任务把这些恢复结果导出为稳定、可审阅、可供后续 Markdown / UI / 导入器消费的新版语义 JSON。

本任务不原地改写旧 repo。恢复结果来自 best-effort 推断时必须保留来源标记，不能伪装成旧数据原生字段。

## 输出

新增 `ChatSessionLegacyUpgradeExporter`，输出 schema：

```text
atelia.chat-session.legacy-upgrade-export.v1
```

顶层字段：

- `schema`
- `generatedAtUtc`：仅显式传入时输出
- `branchName`
- `timeline`
- `recapMappings`
- `warnings`

`timeline` 中每个 commit 输出：

- `ordinal`
- `commit`
- `messageCount`
- `messageCountDeltaFromPrevious`
- `commitMetadata.commitKind`
- `commitMetadata.commitReason`
- `commitMetadata.metadataSource`：`explicit` 或 `legacy-inferred`

`recapMappings` 中每个恢复项输出：

- `kind`：`inferred` 或 `unresolved`
- `mappingSource`：当前固定为 `legacy-inferred`
- `oldHead` / `newHead`
- `recapIndex`
- `recapSourceAnchor`：仅 resolved mapping 输出，字段对齐 Task 01 的 `RecapSourceAnchor`
- `sourceRange`
- `suffixMatchCount`
- `confidence`
- `reason`

## 非目标

- 不生成新的 StateJournal repo。
- 不改写输入 repo 的 durable records / branch refs / reflog。
- 不复制完整消息正文到 JSON 中；需要正文的后续工具应结合原 repo 读取。
- 不把 legacy-inferred metadata 当作 explicit reflog note。

## 验收

- exporter 可从 `ChatSessionLegacyRecapRecovery.Analyze(...)` 的 report 生成稳定 JSON。
- JSON 默认不包含时间戳、不包含绝对 repo 路径。
- compaction finding 会转换为 Task 01 形态的 `recapSourceAnchor`。
- timeline 会输出 `commitKind` / `commitReason` / `metadataSource`。
- explicit note 来源标为 `explicit`；旧数据推断来源标为 `legacy-inferred`。
- 在 `prototypes/Galatea/.atelia/galatea/sessions/cyber-copy` 上可实际导出新版语义 JSON。