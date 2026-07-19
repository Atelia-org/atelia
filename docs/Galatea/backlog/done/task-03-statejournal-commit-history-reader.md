# Task 03: StateJournal Commit History Reader for Offline Tools

> 状态：Done / Design + Prototype
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

设计并原型实现一个 StateJournal 层的离线 commit address reader。初版基于 branch reflog / branch ref v2 `recentHeads`，用于离线分析和救援场景；它枚举的是“曾出现在 branch metadata 中的 `CommitAddress` 候选集”，不是完整 commit DAG，也不默认反序列化对象图。

本任务的语义需要收紧为：reflog-based branch history address enumeration。`oldHead -> newHead` 表示 branch ref transition，通常可反映 main branch 的 head 演化链，但不应被表述为通用 commit parent edge。未来可以在此基础上新增其他救援遍历方案，例如：基于 commit TailMeta v2 的 verified parent walk、基于 segment 扫描的 orphan commit discovery、或基于上层业务索引的恢复策略。

建议能力：

- 枚举某 branch 的 reflog entries，从 `oldHead` / `newHead` 提取完整 `CommitAddress`。
- 枚举 branch ref v2 与 `.json.last` backup 中的 `head` / `recentHeads`，作为 reflog 缺失或损坏时的补充。
- 去重、标准化为 `CommitAddress` 序列，并保留来源信息（例如 reflog line、branch head、recent head）。
- 默认不 load root；上层工具拿到 `CommitAddress` 后自行决定是否通过现有 checkout / branch 派生路径读取对象图。
- 明确标注该 API 是 offline / diagnostic，不保证适合热路径。

## 需要澄清的设计点

1. 是否要公开 reflog entries？
   - 优点：直接服务离线工具。
   - 风险：reflog 原本是恢复辅助，公开后会变成事实 API。

2. 是否要基于 commit metadata v2 做 parent walk？
   - 当前主线已经在 ObjectMap commit TailMeta v2 中保存 parent `CommitAddress?`。
   - 本任务初版仍以 branch metadata 枚举为主，因为它不需要打开 commit、适合快速离线救援。
   - 未来可新增基于 TailMeta v2 的 verified parent walk，作为另一种救援遍历方案，而不是把 reflog transition 误称为 commit parent edge。

3. 是否需要只读 checkout？
   - 当前 `Repository.Open` 独占锁，`CreateBranch(fromCommit)` 会写 branch ref。
   - 离线工具可能更需要“不创建 branch 文件，只临时 load 某 commit”的只读入口。
   - 初版 reader 推荐不继承反序列化职责，只产出 `CommitAddress`；只读 checkout 可作为后续任务独立设计。

## 非目标

- 不要求本任务实现 ChatSession recap 范围推断。
- 不要求本任务实现完整 commit DAG 或严格 parent-chain walk。
- 不要求本任务默认反序列化 root。
- 不要求解决所有历史损坏恢复场景。
- 不要求优化全量反序列化性能。
- 不要求实现 lazy loading。

## 验收

- 有设计说明，明确 reflog-based address reader、recentHeads fallback、future commit metadata walker 的边界。
- 初版 API 能枚举一个测试 repo 中连续多次 commit 的 head 地址，且不需要打开 `Repository`。
- 上层能用枚举出的历史 `CommitAddress` 通过现有路径读取对应 root。
- 覆盖 segment 未轮换与轮换后的基本场景，至少确认 historical segment 能打开。
- 文档明确：object parent ticket 不是完整 commit parent address，不能直接当 commit graph 使用。

## 风险点

- `VersionChainStatus` 中的 parent ticket 是 `SizedPtr`，不包含 segment number；跨文件祖先只保留逻辑信息，不一定能独立定位。
- `Repository.Open` 会尝试维护 segment layout；离线扫描旧 repo 时要小心不要意外改变用户现场，必要时建议对备份副本操作。
- 若公开 reflog，后续格式变更成本会上升。
