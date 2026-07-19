# Task 03a: StateJournal Commit Metadata v2

> 状态：Done / Root-Cause Fix
> 建议执行者：熟悉 `src/StateJournal` commit / segment / branch ref 实现的会话
> 优先级：高。它修补 Task 03 离线历史读取依赖的 commit 父链定位语义。

## 背景

Task 03 需要从 StateJournal repo 层枚举历史 commit，并让 ChatSession 恢复工具能打开每个历史 root。当前代码已经有 repo-level commit identity：`CommitAddress = { segmentNumber, commitTicket }`，branch ref v2、`recentHeads`、reflog 和 `Repository.CreateBranch(name, CommitAddress)` 都已使用完整地址。

真正的设计缺口在 commit parent 语义：ObjectMap version frame 中的 `parentTicket` 只有单个 RBF segment 文件内的 `SizedPtr` / `CommitTicket`，不是完整 `CommitAddress`。segment 轮换后，仅凭 parent ticket 无法定位父 commit 所在 segment。

因此应在 ObjectMap commit frame 的 tail metadata 中新增 commit-level metadata v2，显式保存 parent `CommitAddress?`。Revision 继续负责单 segment 内的对象版本链；跨 segment 的 commit 父链由 repo-level commit metadata 建模。

## 关键文件

- [`src/StateJournal/Revision.cs`](../../../../src/StateJournal/Revision.cs)
- [`src/StateJournal/Revision.Commit.cs`](../../../../src/StateJournal/Revision.Commit.cs)
- [`src/StateJournal/CommitAddress.cs`](../../../../src/StateJournal/CommitAddress.cs)
- [`src/StateJournal/Internal/VersionChain.cs`](../../../../src/StateJournal/Internal/VersionChain.cs)
- [`src/StateJournal/Internal/VersionChainStatus.cs`](../../../../src/StateJournal/Internal/VersionChainStatus.cs)
- [`src/StateJournal/Repository.cs`](../../../../src/StateJournal/Repository.cs)
- [`docs/Galatea/backlog/done/task-03-statejournal-commit-history-reader.md`](task-03-statejournal-commit-history-reader.md)

## 目标

- 扩展 ObjectMap tail metadata 到 v2，保存：
  - graph root local id
  - symbol table local id
  - parent `CommitAddress?`
- `Revision.Open` 读取 v2 metadata 后暴露完整 parent address。
- legacy 8-byte tail metadata 继续可读。
- 对旧单 segment 数据，可把非空 parent ticket 推断为 `CommitAddress(currentSegmentNumber, parentTicket)`。
- 文档和代码注释明确：per-object parent ticket 不是 commit parent address，不能直接作为跨 segment commit graph 使用。

## 建议 API

- 保留 `Revision.HeadAddress` 表达当前 commit 的完整地址。
- 新增 `Revision.HeadParentAddress` 表达父 commit 的完整地址；unborn 或 root commit 返回 `null`。
- 暂时保留 `Revision.HeadParentId` 作为兼容/诊断属性，但其语义应降级为 ticket-only view。

## TailMeta v2 草案

Legacy layout：

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | graph root local id |
| 4 | 4 | symbol table local id |

v2 layout：

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | magic/version marker |
| 4 | 4 | graph root local id |
| 8 | 4 | symbol table local id |
| 12 | 4 | parent segment number, `0` means no parent |
| 16 | 8 | parent commit ticket serialized, `0` means no parent |

规则：

- `parentSegmentNumber == 0 && parentTicket == 0` 表示 no parent。
- 二者必须同时为空或同时有效。
- v2 loader 必须校验 parent `CommitAddress` 的合法性。
- legacy loader 只在同 segment 假设下推断 parent address；无法证明跨 segment 完整性。

## 非目标

- 不实现 Task 03 的 branch reflog / recentHeads offline reader。
- 不把每个 user object version frame 的 parent ticket 升级为完整地址。
- 不承诺完整 DAG walk；本任务只修补 commit frame 的父地址表达。
- 不新增 lazy loading。

## 验收

- 新 commit 写出的 ObjectMap tail metadata 包含 parent `CommitAddress?`。
- 连续 commit 后 reopen，`HeadParentAddress` 等于前一个 `HeadAddress`。
- segment 轮换后 reopen，`HeadParentAddress` 仍指向旧 segment 的父 commit。
- legacy 8-byte metadata 仍可读取；单 segment parent ticket 可推断为同 segment `CommitAddress`。
- 测试明确覆盖 root commit 无 parent、普通连续 commit、rotation 后 parent segment 保留。

## 风险点

- `VersionChainStatus` 的 parent ticket 会继续存在，并且仍服务对象 version chain diff/rebase；不要把它误解成完整 commit graph。
- `ExportTo` / `SaveAs` 的 rebase frame 会保留逻辑祖先信息；v2 parent address 应由 Revision 当前 head 的完整地址提供，而不是只复制 ticket。
- 对真实旧跨 segment 数据，缺失 segment id 无法可靠修复，只能借助 reflog/recentHeads 或外部扫描做 best-effort 推断。
