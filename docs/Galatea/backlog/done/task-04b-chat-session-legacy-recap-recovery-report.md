# Task 04b: ChatSession Legacy Recap Recovery Report

> 状态：Done
> 建议执行者：熟悉 `prototypes/ChatSession` 与 Task 02 history reader 的实现会话
> 依赖：Task 04a 的 StateJournal readonly commit checkout API（已完成）

## 背景

旧 ChatSession recap record 没有 `RecapSourceAnchor`。Task 04b 在只读读取历史 commit 的基础上，比较相邻 ChatSession message snapshots，保守推断一次 compaction 是否发生，并输出结构化 finding/report。

本任务先增强可解释性：在推断 compaction 之前，先输出 effective commit timeline。每个 commit 显示 message count、最旧 3 条 message signature、最新 3 条 message signature，用于人工判断每次提交大致是 completion、compaction、revert/reroll，还是 context/header 调整。

## 目标

实现 ChatSession 层只读恢复报告器：

- 使用 `RepositoryHistoryReader` 获取历史 commit address；默认使用 effective parent-chain，必要时再参考 raw candidates 做救援诊断。
- 使用 Task 04a API 读取每个历史 commit 的 ChatSession root/messages。
- 将 messages 序列转成稳定 signature：`{kind}:{stableHash}:{preview}`。
- 第一层输出 timeline report：每个 commit 的 message count、oldest3 signatures、newest3 signatures、相邻 delta。
- 第一层同时输出每个 commit 的保守 attribution：`initial-state`、`model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`redundant-save` 或 `other`。
- 第二层比较相邻 snapshots，识别疑似 compaction。
- 返回结构化 report，不修改 repo，不输出 sidecar 文件。

Timeline 输出字段：

| 字段 | 含义 |
|---|---|
| `ordinal` | old → new 顺序编号 |
| `commit` | `CommitAddress.ToString()` |
| `source` | `EffectiveHead` / `EffectiveParent` |
| `messageCount` | 当前 commit 的 messages 条数 |
| `messageCountDeltaFromPrevious` | 相对上一条 timeline entry 的数量变化；第一条为 null |
| `attribution` | 对该 commit 的保守归因，包含 kind 与 reason |
| `oldest3` | 最旧 3 条 message signature |
| `newest3` | 最新 3 条 message signature |

Message signature 规则：

- 格式固定为 `{kind}:{stableHash}:{preview}`。
- `stableHash` 使用稳定 hash（例如 SHA-256 截断 8-12 hex），不要使用进程随机化的 `string.GetHashCode()`。
- `preview` 使用首尾裁剪文本，转义换行；为空正文也要显示 kind/hash。
- action/tool-results/context-header 需要把 tool call id、tool name、raw arguments、tool result status 等纳入 hash 输入，避免静默丢结构信息。

Compaction finding 建议输出字段：

| 字段 | 含义 |
|---|---|
| `oldHead` | compaction 前候选 head |
| `newHead` | compaction 后候选 head |
| `recapIndex` | new messages 中疑似 recap 的 index |
| `sourceStartIndex` | 推断的 old source 起点，通常为 `0` |
| `sourceEndExclusive` | 推断的 old source 终点 |
| `sourceMessageCountBefore` | old messages 总数 |
| `suffixMatchCount` | old/new 尾部匹配消息数量 |
| `confidence` | `high` / `medium` / `low` / `unresolved` |
| `reason` | 人类可读解释 |

## 推断约束

- Timeline 默认按 old → new 输出；`RepositoryHistoryReader` 的 effective parent-chain 若返回 head → parent，reporter 需要反转用于 delta 分析。
- 第一版只识别“new 比 old 短，new 前部存在无 anchor recap，old/new 尾部有稳定公共后缀”的保守情形。
- `ContextHeader` 可在 compaction 后重新 prepend；算法必须允许 `headers + recap + suffix`。
- 不确定时输出 unresolved，不要制造 source anchor。
- 普通 append、reroll、rewind 不应误判为 compaction。
- 纯尾部回退应归因为 `revert-turn`，不输出 unresolved compaction finding。
- 相邻 commit 的完整 message signature 序列完全相同时继续比较 root `systemPrompt`：若变化则归因为 `update-system-prompt`，若也相同则归因为 `redundant-save`。

## 非目标

- 不输出 sidecar JSON / Markdown。
- 不修改 repo。
- 不展开 recap。
- 不处理非 ChatSession root schema。

## 验收

- 能输出 effective commit timeline，每条包含 message count、oldest3 signatures、newest3 signatures。
- signature 格式为 `{kind}:{stableHash}:{preview}`，hash 稳定且 preview 转义换行。
- 普通 completion commit 在 timeline 中体现 message count 增量与尾部 signature 变化。
- 普通 completion commit 归因为 `model-turn`。
- compaction commit 在 timeline 中体现 message count 下降与 leading recap signature。
- compaction commit 归因为 `compaction`。
- 对测试 repo 中一次 compaction 输出 high-confidence finding。
- 带 leading `ContextHeader` 的 compaction 能正确识别 recap index 与 source range。
- 普通追加 commit 不产生 compaction finding。
- rewind 或无法建立相邻关系时输出 unresolved / warning，而不是误判。
- 报告包含 `RepositoryHistoryReader` warnings。

## 实施结果

- 已新增 `ChatSessionLegacyRecapRecovery.Analyze(repoDir, branchName)`。
- 默认使用 `RepositoryHistoryReader.EnumerateBranchEffectiveCommitAddresses(...)` 获取当前 HEAD 的 effective parent-chain，并按 old → new 输出 timeline。
- 每个 timeline entry 包含 `messageCount`、相邻 `messageCountDeltaFromPrevious`、`oldest3`、`newest3`。
- 每个 timeline entry 包含 `attribution`，当前可识别 `initial-state`、`model-turn`、`compaction`、`revert-turn`、`update-system-prompt`、`redundant-save`，其他复杂变化标为 `other`。
- message signature 格式为 `{kind}:{stableHash}:{preview}`，其中 stable hash 为 SHA-256 截断 12 hex，preview 转义换行并首尾裁剪。
- 已新增 `ChatSessionLegacyRecapRecovery.FormatText(report)`，用于简单打印/输出文本报告。
- 已实现保守 compaction finding：当 new snapshot 更短、前部存在 recap、尾部 suffix 匹配时输出 high-confidence finding。

已验证：

```bash
dotnet test tests/FamilyChat.Server.Tests/FamilyChat.Server.Tests.csproj --filter "FullyQualifiedName~ChatSessionLegacyRecapRecovery"
```

## 后续目标回顾

- Task 04c：把 report 输出为 sidecar JSON / Markdown，供人工审阅与后续导出工具消费。
- 新数据应在 ChatSession commit metadata 中显式记录 commit purpose / commit type；legacy attribution 只是恢复旧数据的推断层。
- 可选后续：如果实际旧 repo 出现 rewind / reroll 难以解释，可在 StateJournal 层补 raw reflog transition API，辅助诊断不在 effective parent-chain 上的短旁支。
- 可选后续：将 sidecar 与 ChatSession Markdown export 结合，实现“当前 HEAD recap + 推断 source range”的关联导出。
- 仍然不建议原地改写旧 repo；legacy recovery 结果应优先以 sidecar / report 形式保留 best-effort 语义。
