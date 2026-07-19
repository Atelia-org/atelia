# Task 04: Legacy ChatSession Recap Recovery and Upgrade

> 状态：Done / Epic
> 建议执行者：已分阶段完成；04a 由 StateJournal 层提供历史 root 加载能力，04b-04f 由 ChatSession/Galatea 层闭合恢复、导出、replay 与人读 Markdown。
> 依赖：Task 01 / 02 / 03 / 03a / 04a / 04b / 04c / 04d / 04e / 04f / 06 已完成。

## 背景

旧 Galatea / ChatSession 会话中的 recap 没有 source anchor。未来新增 anchor 只能保证新数据；旧数据若要导出完整原始历史，需要从历史 commit 中推断 recap 替换了哪段消息。

这个任务是 best-effort 离线恢复，不应把推断结果伪装成绝对事实。

## 关键文件

- [`prototypes/ChatSession/MessageRecord.cs`](../../../../prototypes/ChatSession/MessageRecord.cs)
- [`prototypes/ChatSession/ChatSessionEngine.Compaction.cs`](../../../../prototypes/ChatSession/ChatSessionEngine.Compaction.cs)
- [`prototypes/ChatSession/ChatSessionHistory.cs`](../../../../prototypes/ChatSession/ChatSessionHistory.cs)
- [`src/StateJournal/Repository.BranchRefs.cs`](../../../../src/StateJournal/Repository.BranchRefs.cs)
- [`src/StateJournal/RepositoryHistoryReader.cs`](../../../../src/StateJournal/RepositoryHistoryReader.cs)
- [`src/StateJournal/CommitAddress.cs`](../../../../src/StateJournal/CommitAddress.cs)
- [`docs/Galatea/backlog/done/task-03-statejournal-commit-history-reader.md`](../done/task-03-statejournal-commit-history-reader.md)
- [`docs/Galatea/backlog/done/task-03a-statejournal-commit-metadata-v2.md`](../done/task-03a-statejournal-commit-metadata-v2.md)
- [`docs/Galatea/backlog/done/feature-request-recap-source-range-anchors.md`](../done/feature-request-recap-source-range-anchors.md)

## 目标

构建一个离线恢复链路：扫描 ChatSession repo 的历史 heads，读取历史 commit 的 messages 序列，比较相邻 commit，推断无 anchor recap 的 source range，并输出报告或 sidecar。恢复结果必须标注为 best-effort，不应伪装成绝对事实。

随着 04c 的实测，本链路也承担了每次提交的 legacy attribution：把 commit 保守归因为 `model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`redundant-save` 或 `other`。这只是旧数据恢复层；后续 ChatSession 应在提交时显式记录 commit purpose / commit type，避免新数据继续依赖推断。

拆分任务：

1. [`task-04a-statejournal-readonly-commit-checkout.md`](../done/task-04a-statejournal-readonly-commit-checkout.md)：StateJournal 层新增只读历史 commit checkout / load API，不创建 branch、不写 refs/reflog。
2. [`task-04b-chat-session-legacy-recap-recovery-report.md`](../done/task-04b-chat-session-legacy-recap-recovery-report.md)：ChatSession 层实现只读恢复报告器，识别疑似 compaction。
3. [`task-04c-chat-session-recap-recovery-sidecar-export.md`](../done/task-04c-chat-session-recap-recovery-sidecar-export.md)：把 04b 的 finding 输出为 JSON sidecar，供导出器和人工审阅使用。
4. [`task-04d-chat-session-legacy-upgrade-export.md`](../done/task-04d-chat-session-legacy-upgrade-export.md)：导出新版语义 JSON，补齐 legacy recap source mapping 与 commit metadata。默认不原地修改输入 repo。
5. [`task-04e-chat-session-event-source-replay-migration.md`](task-04e-chat-session-event-source-replay-migration.md)：把 04d JSON 扩展为可 replay 的 semantic event source，并导入为新版 StateJournal repo。
6. 04f：从 `chat-session-legacy-upgrade-export.json` 生成面向人工阅读和搜索的 Markdown。JSON 仍是机读事实源；Markdown 抽取 system prompt、observation、action flattened text 与 recap 内容，按 replay 顺序输出为纯文本 code fence 流，便于检索、审阅和对比。fence 至少使用 6 个 `~`；若内容中出现更长连续 `~`，则自动使用更长 fence，避免内容意外闭合 fence。

最终决策：不要为了恢复结果原地改写旧 repo；优先输出 JSON event source，并可选择 replay 到新的 upgraded repo。best-effort 推断保留在 legacy export 的 `metadataSource` / recap mapping 中；新 replay repo 写入 explicit commit metadata。

新增决策：后续新写入的 ChatSession commit 应携带显式 commit metadata，例如 `model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`redundant-save`。legacy sidecar 中的 attribution 字段用于填补旧数据缺口，也可作为未来 metadata schema 的样例。

Task 06 已完成：[`task-06-chat-session-explicit-commit-kind-metadata.md`](../done/task-06-chat-session-explicit-commit-kind-metadata.md)。

## 推断思路

典型 compaction 前后：

- 旧序列：`prefix + suffix`
- 新序列：`recap + suffix`，若有 protected `ContextHeader`，则可能是 `headers + recap + suffix`
- `suffix` 应能通过消息内容、kind、tool call id 或 action block JSON 识别为同一批消息。
- 被替换范围通常是旧序列的 `[0, splitIndex)`，但 context header 处理会让前段形态更复杂。

可以先做保守匹配：

- 只处理相邻 heads 中“新序列比旧序列短，且新序列包含 leading recap”的情况。
- 用 suffix 最长公共后缀匹配确认 recent history 未变。
- 对 action/tool results 使用稳定序列化内容或 flattened text + tool ids。
- 匹配置信度不足时输出 unresolved，不写升级结果。

## 非目标

- 不要求 100% 恢复所有旧会话。
- 不要求在原 repo 上原地改写。
- 不要求修改 StateJournal 持久化格式。
- 不要求处理任意上层 schema，只处理 ChatSession root schema。
- Task 04a 不要求理解 ChatSession；它只提供 StateJournal 只读历史 root 加载能力。
- Task 04b 不要求输出 sidecar 文件；先返回结构化 finding/report 即可。

## 验收

- 04a：能从指定 `CommitAddress` 只读加载历史 root，不创建 branch、不改写 branch ref / backup / reflog。
- 能对一个包含 compaction 的测试 repo 识别出疑似 recap source range。
- 能输出人类可审阅报告，包含 old head、new head、旧范围、新 recap index、置信度。
- 对无法判定的 recap 明确标记 unresolved。
- 不会修改输入 repo。
- 有测试覆盖：普通 commit 不应误判为 compaction；rewind 或普通追加不应误判。
- 有测试覆盖：普通 completion 归因为 `model-turn`；纯尾部回退归因为 `revert-turn`，且不输出 unresolved compaction finding。
- 有测试覆盖：完整 message signature 序列不变但 root `systemPrompt` 变化的相邻 commit 归因为 `update-system-prompt`；二者都相同才归因为 `redundant-save`。
- 能从 `chat-session-legacy-upgrade-export.json` 生成 Markdown，且系统提示词、用户观察、模型行动与 recap 都以纯文本 code fence 表示，避免 JSON 转义影响阅读和搜索。

## 风险点

- 旧数据可能缺 reflog 或 reflog 不完整。
- `RepositoryHistoryReader` 已区分 raw candidates 与 effective parent-chain；04b 若需要更稳的相邻关系（例如保留 reflog transition 边），可回到 StateJournal 层追加 transition API。
- 内容同一性可能受 sanitization、thinking strip、tool result flattening 影响。
- protected `ContextHeader` 会在 compaction 后重新 prepend，比较逻辑不能只看 index。
- StateJournal 当前没有 lazy loading，全量 checkout 历史 heads 的成本可能很高；这是离线工具可接受的代价，但要在文档中说清楚。
