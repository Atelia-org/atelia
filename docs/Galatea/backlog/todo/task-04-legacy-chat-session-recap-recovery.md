# Task 04: Legacy ChatSession Recap Recovery and Upgrade

> 状态：Todo / Research Prototype
> 建议执行者：适合做离线分析工具的新会话
> 依赖：最好先有 Task 03 的 commit history reader；没有也可直接解析 branch reflog 做原型。

## 背景

旧 Galatea / ChatSession 会话中的 recap 没有 source anchor。未来新增 anchor 只能保证新数据；旧数据若要导出完整原始历史，需要从历史 commit 中推断 recap 替换了哪段消息。

这个任务是 best-effort 离线恢复，不应把推断结果伪装成绝对事实。

## 关键文件

- [`prototypes/ChatSession/MessageRecord.cs`](../../../../prototypes/ChatSession/MessageRecord.cs)
- [`prototypes/ChatSession/ChatSessionEngine.Compaction.cs`](../../../../prototypes/ChatSession/ChatSessionEngine.Compaction.cs)
- [`src/StateJournal/Repository.BranchRefs.cs`](../../../../src/StateJournal/Repository.BranchRefs.cs)
- [`src/StateJournal/CommitAddress.cs`](../../../../src/StateJournal/CommitAddress.cs)
- [`docs/Galatea/backlog/todo/task-03-statejournal-commit-history-reader.md`](task-03-statejournal-commit-history-reader.md)
- [`docs/Galatea/backlog/todo/feature-request-recap-source-range-anchors.md`](feature-request-recap-source-range-anchors.md)

## 目标

构建一个离线恢复器：扫描 ChatSession repo 的历史 heads，比较相邻 commit 中的 messages 序列，推断无 anchor recap 的 source range，并输出报告或升级后的数据。

建议阶段：

1. 只读分析报告：列出每次疑似 compaction。
2. 生成 sidecar JSON / Markdown：记录推断出的 recap source range。
3. 可选升级：写出新的兼容 repo 或导出文件，而不是直接修改原 repo。

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

## 验收

- 能对一个包含 compaction 的测试 repo 识别出疑似 recap source range。
- 能输出人类可审阅报告，包含 old head、new head、旧范围、新 recap index、置信度。
- 对无法判定的 recap 明确标记 unresolved。
- 不会修改输入 repo，默认要求用户对备份副本运行。
- 有测试覆盖：普通 commit 不应误判为 compaction；rewind 或普通追加不应误判。

## 风险点

- 旧数据可能缺 reflog 或 reflog 不完整。
- 内容同一性可能受 sanitization、thinking strip、tool result flattening 影响。
- protected `ContextHeader` 会在 compaction 后重新 prepend，比较逻辑不能只看 index。
- StateJournal 当前没有 lazy loading，全量 checkout 历史 heads 的成本可能很高；这是离线工具可接受的代价，但要在文档中说清楚。
