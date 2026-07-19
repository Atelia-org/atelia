# Task 04a: StateJournal Readonly Commit Checkout

> 状态：Done
> 建议执行者：刚完成 `RepositoryHistoryReader` / 熟悉 StateJournal repository loading 的会话
> 上游：Task 03 / Task 03a
> 下游：Task 04b Legacy ChatSession Recap Recovery Report

## 背景

Task 04 需要比较 ChatSession repo 中多个历史 HEAD 的 root graph。`RepositoryHistoryReader` 已能从 branch ref / backup / reflog 中枚举历史 `CommitAddress`，但当前公开读取历史 root 的可用路径主要是 `Repository.CreateBranch(name, CommitAddress)`。

`CreateBranch(fromCommit)` 会写 branch ref / backup / reflog，不适合作为离线恢复工具读取原 repo 的默认路径。Task 04a 先补一个更干净的 StateJournal API：从指定 `CommitAddress` 只读加载历史 root，不创建 branch，不推进 head，不写 metadata。

## 目标

新增 StateJournal 层只读历史 commit checkout / load API。API 名称可按实现调整，但语义必须清楚表达：

- 输入 `CommitAddress`。
- 加载该 commit 对应的 root object graph。
- 返回可读取的 `Revision` 或 root object 视图。
- 不创建 branch。
- 不写 branch ref、`.last` backup、reflog。
- 不允许通过该只读视图 commit。

示意 API：

```csharp
public AteliaResult<Revision> CheckoutCommitReadonly(CommitAddress commitAddress);
```

如果现有 `Revision` 类型难以标记只读，也可先提供更窄的 API：

```csharp
public AteliaResult<DurableObject> LoadRootAtCommit(CommitAddress commitAddress);
```

具体形态由实现者按 StateJournal 内部结构决定。

## 实施结果

已新增 `Repository.LoadRootAtCommit(CommitAddress)`。该 API 复用现有 `Revision.Open` / historical segment 打开路径加载指定 commit 的 graph root，返回 detached diagnostic root object，不创建 branch，不写 branch ref / `.last` backup / reflog，也不推进任何 branch head。

这里的“只读”定义为 repository 持久化层只读：返回 root 所属的 `Revision` 不绑定 branch；调用 `Repository.Commit(root)` 会按现有 branch ownership 校验失败。因此该 API 适合离线工具读取历史对象图，不适合继续演化历史 commit。返回的 durable object 仍是普通内存对象，调用方不应把它理解成不可变 facade；若未来需要更强的对象级不可变视图，可另行设计只读 wrapper 或 frozen materialization。

新增测试覆盖：

- 连续 commit 后读取非当前 HEAD 的历史 root。
- 与 `RepositoryHistoryReader.EnumerateBranchRawCommitAddresses(...)` / `EnumerateBranchEffectiveCommitAddresses(...)` 配合读取枚举出的历史 address。
- 调用前后 branch ref / backup / reflog bytes 完全不变。
- missing segment address 返回 failure 且不污染 metadata / head。
- existing segment + invalid ticket 返回 failure 且不污染 metadata / head。
- segment rotation 后读取非 active segment 中的历史 root。

## 设计约束

- 不能复用会写 branch metadata 的 `CreateBranch(fromCommit)` 作为最终实现。
- 不能修改原 repo 的 refs / branches / reflog / backup 文件。
- 若返回 `Revision`，应避免调用者误以为可以 commit；可以通过只读标志、独立类型或文档化限制实现。
- 应复用现有 commit load / graph root 恢复逻辑，避免复制底层 frame 解析。
- API 面向 offline / diagnostic，不要求热路径性能。

## 验收

- 测试 repo 连续提交多次后，可用历史 `CommitAddress` 读取对应旧 root。
- 调用只读 checkout/load 前后，branch ref 文件、`.last` backup、reflog 内容不变。
- 读取不存在或损坏的 commit address 时返回明确 failure，不污染 repository 状态。
- 能与 `RepositoryHistoryReader.EnumerateBranchRawCommitAddresses(...)` / `EnumerateBranchEffectiveCommitAddresses(...)` 配合：枚举出的历史地址可被只读 API 加载。
- 覆盖至少一个非当前 HEAD 的历史 commit。

## 非目标

- 不要求实现 ChatSession recap 恢复逻辑。
- 不要求实现完整 commit DAG 遍历。
- 不要求实现 lazy loading。
- 不要求解决并发读取在线 repo 的锁策略；离线工具仍可要求对备份副本运行。

## 风险点

- 当前 `Repository.Open` 会拿 repo lock；只读 commit checkout 仍可能处在独占打开模型下。
- `Revision` 是可编辑工作态；本任务选择返回 root object 而不是 public checkout `Revision`，并确保它不绑定 branch，不能通过 `Repository.Commit(root)` 推进 head。
- 历史 commit 可能位于旧 segment；测试需要覆盖 segment number 不等于当前 active segment 的情况。
