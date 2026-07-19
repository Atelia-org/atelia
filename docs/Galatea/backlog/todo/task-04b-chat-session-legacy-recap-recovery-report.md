# Task 04b: ChatSession Legacy Recap Recovery Report

> 状态：Todo / Ready
> 建议执行者：熟悉 `prototypes/ChatSession` 与 Task 02 history reader 的实现会话
> 依赖：Task 04a 的 StateJournal readonly commit checkout API（已完成）

## 背景

旧 ChatSession recap record 没有 `RecapSourceAnchor`。Task 04b 在只读读取历史 commit 的基础上，比较相邻 ChatSession message snapshots，保守推断一次 compaction 是否发生，并输出结构化 finding/report。

## 目标

实现 ChatSession 层只读恢复报告器：

- 使用 `RepositoryHistoryReader` 获取历史 commit address；默认优先使用 effective parent-chain，必要时再参考 raw candidates 做救援诊断。
- 使用 Task 04a API 读取每个历史 commit 的 ChatSession root/messages。
- 将 messages 序列转成稳定 fingerprint。
- 比较相邻 snapshots，识别疑似 compaction。
- 返回结构化 report，不修改 repo，不输出 sidecar 文件。

建议输出字段：

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

- 第一版只识别“new 比 old 短，new 前部存在无 anchor recap，old/new 尾部有稳定公共后缀”的保守情形。
- `ContextHeader` 可在 compaction 后重新 prepend；算法必须允许 `headers + recap + suffix`。
- 不确定时输出 unresolved，不要制造 source anchor。
- 普通 append、reroll、rewind 不应误判为 compaction。

## 非目标

- 不输出 sidecar JSON / Markdown。
- 不修改 repo。
- 不展开 recap。
- 不处理非 ChatSession root schema。

## 验收

- 对测试 repo 中一次 compaction 输出 high-confidence finding。
- 带 leading `ContextHeader` 的 compaction 能正确识别 recap index 与 source range。
- 普通追加 commit 不产生 compaction finding。
- rewind 或无法建立相邻关系时输出 unresolved / warning，而不是误判。
- 报告包含 `RepositoryHistoryReader` warnings。
