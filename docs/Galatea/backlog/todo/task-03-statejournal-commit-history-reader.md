# Task 03: StateJournal Commit History Reader for Offline Tools

> 状态：Todo / Design + Prototype
> 建议执行者：熟悉 `src/StateJournal` 的实现会话
> 优先级：中高。它是旧数据恢复、诊断和离线升级的基础设施。

## 背景

ChatSession 的 recap source anchor 能解决未来数据的精确追溯问题，但旧 Galatea 会话已经存在无 anchor recap。为了离线恢复旧数据，需要一种从 StateJournal repo 层枚举历史 commit / historical heads 的能力，然后由 ChatSession 层解释每个 root 的消息序列变化。

当前 StateJournal 已有若干相关事实：

- branch ref / reflog 中记录 `OldHead` 与 `NewHead`。
- branch ref v2 中有 `recentHeads`。
- `Repository.CreateBranch(name, CommitAddress)` 可从指定历史 commit 派生 branch。
- object version frame 中有 per-object parent ticket，但它不是完整 `CommitAddress`。
- segment 轮换后，完整定位需要 `segmentNumber + ticket`。

## 关键文件

- [`src/StateJournal/Repository.cs`](../../../../src/StateJournal/Repository.cs)
- [`src/StateJournal/Repository.BranchRefs.cs`](../../../../src/StateJournal/Repository.BranchRefs.cs)
- [`src/StateJournal/Repository.Segments.cs`](../../../../src/StateJournal/Repository.Segments.cs)
- [`src/StateJournal/CommitAddress.cs`](../../../../src/StateJournal/CommitAddress.cs)
- [`src/StateJournal/Revision.Commit.cs`](../../../../src/StateJournal/Revision.Commit.cs)
- [`src/StateJournal/Internal/VersionChainStatus.cs`](../../../../src/StateJournal/Internal/VersionChainStatus.cs)
- [`docs/StateJournal/usage-guide.md`](../../../StateJournal/usage-guide.md)

## 目标

设计并原型实现一个 StateJournal 层的离线 commit history reader。初版可以基于 branch reflog / recentHeads，而不是承诺完整 DAG。

建议能力：

- 枚举某 branch 的 reflog entries，得到按时间或 generation 排序的 `oldHead -> newHead`。
- 枚举 branch ref 中的 `recentHeads`。
- 去重、标准化为 `CommitAddress` 序列。
- 提供从 `CommitAddress` 临时读取 root 的 helper，供上层比较。
- 明确标注该 API 是 offline / diagnostic，不保证适合热路径。

## 需要澄清的设计点

1. 是否要公开 reflog entries？
   - 优点：直接服务离线工具。
   - 风险：reflog 原本是恢复辅助，公开后会变成事实 API。

2. 是否要新增真正的 commit metadata frame？
   - 现状：ObjectMap tail meta 只保存 graph root local id 与 symbol table local id。
   - 可能增强：保存 parent `CommitAddress?`、branch name、note、timestamp。
   - 这会让未来 commit graph walk 更规范，但涉及 StateJournal 格式演进。

3. 是否需要只读 checkout？
   - 当前 `Repository.Open` 独占锁，`CreateBranch(fromCommit)` 会写 branch ref。
   - 离线工具可能更需要“不创建 branch 文件，只临时 load 某 commit”的只读入口。

## 非目标

- 不要求本任务实现 ChatSession recap 范围推断。
- 不要求解决所有历史损坏恢复场景。
- 不要求优化全量反序列化性能。
- 不要求实现 lazy loading。

## 验收

- 有设计说明，明确 reflog-based reader 与 future commit metadata 的边界。
- 初版 API 能枚举一个测试 repo 中连续多次 commit 的 head 地址。
- 能从枚举出的历史 `CommitAddress` 读取对应 root。
- 覆盖 segment 未轮换与轮换后的基本场景，至少确认 historical segment 能打开。
- 文档明确：object parent ticket 不是完整 commit parent address，不能直接当 commit graph 使用。

## 风险点

- `VersionChainStatus` 中的 parent ticket 是 `SizedPtr`，不包含 segment number；跨文件祖先只保留逻辑信息，不一定能独立定位。
- `Repository.Open` 会尝试维护 segment layout；离线扫描旧 repo 时要小心不要意外改变用户现场，必要时建议对备份副本操作。
- 若公开 reflog，后续格式变更成本会上升。
