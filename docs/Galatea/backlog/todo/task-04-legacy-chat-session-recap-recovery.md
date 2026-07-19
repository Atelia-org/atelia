# Task 04: Legacy ChatSession Recap Recovery and Upgrade

> 状态：Todo / Epic
> 建议执行者：分阶段执行；先由熟悉 StateJournal 的会话完成 04a，再由 ChatSession/Galatea 会话完成 04b/04c
> 依赖：Task 01 / 02 / 03 / 03a / 04a 已完成；下一步做 Task 04b 的 ChatSession 恢复报告器。

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

拆分任务：

1. [`task-04a-statejournal-readonly-commit-checkout.md`](../done/task-04a-statejournal-readonly-commit-checkout.md)：StateJournal 层新增只读历史 commit checkout / load API，不创建 branch、不写 refs/reflog。
2. [`task-04b-chat-session-legacy-recap-recovery-report.md`](task-04b-chat-session-legacy-recap-recovery-report.md)：ChatSession 层实现只读恢复报告器，识别疑似 compaction。
3. [`task-04c-chat-session-recap-recovery-sidecar-export.md`](task-04c-chat-session-recap-recovery-sidecar-export.md)：把 04b 的 finding 输出为 sidecar JSON / Markdown，供导出器和人工审阅使用。
4. 可选后续：升级/迁移工具。默认不原地修改输入 repo。

当前决策：先做 04a。不要为了 04b 在 ChatSession 层复制 repo 到 temp dir 再 `CreateBranch(fromCommit)`；那是可行但不够干净的备选方案。优先补 StateJournal 只读 API。

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

## 风险点

- 旧数据可能缺 reflog 或 reflog 不完整。
- `RepositoryHistoryReader` 已区分 raw candidates 与 effective parent-chain；04b 若需要更稳的相邻关系（例如保留 reflog transition 边），可回到 StateJournal 层追加 transition API。
- 内容同一性可能受 sanitization、thinking strip、tool result flattening 影响。
- protected `ContextHeader` 会在 compaction 后重新 prepend，比较逻辑不能只看 index。
- StateJournal 当前没有 lazy loading，全量 checkout 历史 heads 的成本可能很高；这是离线工具可接受的代价，但要在文档中说清楚。
