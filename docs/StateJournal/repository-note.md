# Repository 专项笔记

> 日期：2026-03-20
> 状态：已与当前 `Repository` 实现对齐（含 recent window + archive bucket）

---

## 0. 这份笔记现在回答什么问题

`Repository` 不再被理解为"一个固定 `Revision Main` 加一个当前 `.sj.rbf` 文件"的薄包装。

新的目标模型是：

- 整个 repo 在逻辑上是一组**命名 branch**，每个 branch 指向特定 segment 中的一个 commit
- `.sj.rbf` 是**物理分段**，按编号（1-based）管理
- `refs/branches/*.json` 是当前可提交工作线的命名 branch 指针，记录 `{segmentNumber, ticket}`
- `Revision` 是一个**已打开的可编辑工作会话**，而不是 repo 自身，也不是 commit 本身

### 2026-03-20 重大简化：消除 CommitSequence

> 详见 `docs/StateJournal/eliminate-commit-sequence.md`

之前的设计中存在全局递增 `CommitSequence` 作为 commit 的 surrogate key，并需要 `sequence.json` 持久化、三路崩溃恢复等机制。
现在这一切已被消除。每个 commit 的天然地址是独立的 `CommitAddress(uint SegmentNumber, CommitTicket)`——就像 `{楼号, 房间号}` 天然唯一。

消除后的收益：
- `sequence.json` 文件不再存在
- `Open()` 恢复流程从"三路 recovery"简化为"扫描 segment + 加载 branch"
- Segment 路由在 recent window 内保持 O(1) 偏移定位；archive 段按 segment number 直接推导路径
- TailMeta 从 12 字节缩减为 4 字节（仅需存 GraphRoot LocalId）

---

## 1. 核心模型

### 1.1 Repository

`Repository` 是 repo 级协调者，负责：

- 进程独占锁（`state-journal.lock`）
- `refs/branches/` 的发现、加载与原子更新
- active segment 的选择与轮换
- active segment 可写句柄的长期持有
- 历史 segment 的按次只读打开
- 已加载 `Revision` 实例的生命周期管理

### 1.2 Revision

`Revision` 表示：

- 从某个已提交 commit 打开的内存对象图
- 可编辑、可 commit
- 持有自己的 `_head` 视角
- 只记录自己当前绑定的 `segmentNumber`
- 持有 `BranchName`（internal set-once 属性），由 `Repository` 在绑定时设置一次

因此它更接近"工作会话 / checkout / workspace"，而不是"commit 本身"。

当前实现中，`Revision` **不再长期持有 `IRbfFile`**。
文件句柄生命周期收敛到 `Repository` / `SegmentCatalog`：

- active segment 的可写 `IRbfFile` 由 `Repository` 长期持有
- 历史 segment 在 `CheckoutBranch` / `Revision.Open(...)` 时按次只读打开，加载完成后立即关闭
- `Revision.Commit(...)` / `Revision.SaveAs(...)` 所需的 `IRbfFile` 由调用方显式传入

这样把"对象图工作态"与"文件句柄生命周期"解耦，recent/archive 维护也不再被已加载 `Revision` 长期阻塞。

`BranchName` 的存在使 `Repository.Commit(graphRoot)` 能从 `graphRoot.Revision.BranchName` O(1) 定位所属 branch，避免遍历所有 branch 做 `ReferenceEquals` 匹配。

### 1.3 Branch

`branch` 是一个命名的可移动提交线，例如：

- `draft/foo`
- `autosave/bar`
- `main`

- 每个 branch 文件记录该名字当前指向的 segment 与 commit（通过 `CommitAddress`）。

`Repository` 提供显式的按名打开入口：

- `CheckoutBranch("main")`
- `CheckoutBranch("draft/foo")`

以及显式的 branch 创建入口：

- `CreateBranch("main")`：显式创建主工作线
- `CreateBranch("feature")`：创建一个空的 unborn branch
- `CreateBranch("feature", "main")`：从 `main` 的当前已提交 HEAD 派生一个新 branch

这里的"派生"只复制源 branch 的已提交 HEAD，不复制未提交工作态；新 branch 应拿到独立的 `Revision` 实例。

#### Branch 名称验证

`Repository.ValidateBranchName(string?)` 是 public static 纯函数，采用**白名单策略**：

- 合法字符：`[a-zA-Z0-9._-/]`
- 必须以字母或数字开头
- 不得以 `/` `.` `-` 结尾
- 不得含连续 `//`
- 每个 `/` 分隔的 component 不得为 `.` 或 `..`（路径穿越）
- component 不得以 `.lock` 结尾（与 git 兼容）
- 长度 1–256

`CreateBranch` 在创建前调用此验证；`GetBranchFilePath` 内含 containment 安全网——即使验证规则遗漏了某种穿越 pattern，组合路径后的 `Path.GetFullPath` 检查也会阻止写出 `branches` 目录之外。

---

## 2. 目录结构

```text
{repoDir}/
  state-journal.lock              # 身份标记 + 进程独占锁
  refs/
    branches/                    # 当前可写工作线；Repository 只扫描这一层
      draft/
        foo.json
    tags/                        # 预留给未来只读命名标签；当前阶段可不存在
  recent/
    00000005.sj.rbf              # 最新连续后缀窗口；不要求从 1 起始
    00000006.sj.rbf
    00000007.sj.rbf
  archive/
    00000001-00000200/
      00000001.sj.rbf
      00000002.sj.rbf
```

说明：

- `refs/` 是总命名空间根。
- `refs/branches/` 是当前唯一由 `Repository` 读写的 movable branch 层。
- `refs/tags/` 预留给未来只读标签；当前阶段不创建、不扫描。
- `recent/` 是共享 segment 的最新连续后缀窗口，不属于某个单独 branch。
- `archive/` 保存更老的 segment，按固定 bucket 目录组织。

---

## 3. branch 文件格式

每个 `refs/branches/*.json` 保存二元组：

```json
{
  "version": 1,
  "segmentNumber": 2,
  "ticket": 1234567890
}
```

语义：

- `segmentNumber`：该 branch 当前指向的 commit 所在 segment 的编号（`uint`，1-based）
- `ticket`：该 commit 的 `SizedPtr.Serialize()` 值（ObjectMap 帧在 segment 内的物理位置）

刻意不保存 segment 文件路径，因为 `segmentNumber → 文件` 可由 recent window 偏移或 archive bucket 规则直接推出。

---

## 4. Segment 命名与路由

### 4.1 基本规则

每个 `.sj.rbf` 文件名等于该 segment 的编号（`uint`，1-based hex8）：

```text
00000001.sj.rbf    # segment #1
00000002.sj.rbf    # segment #2
```

归档目录采用固定 bucket 命名：

```text
archive/00000001-00000200/00000001.sj.rbf
archive/00000001-00000200/00000200.sj.rbf
archive/00000201-00000400/00000201.sj.rbf
```

### 4.2 必须成立的不变量

- 全 repo 任意时刻只有一个 active segment（最后一个）。
- active segment 接收所有 branch 的新 commit。
- 一个 commit 只写入一个 segment。
- 新 segment 一旦启用，后续所有 commit 都只进入这个新 segment。
- 全 repo 的 segment 编号全局连续、无空洞。
- `recent/` 必须是“最新连续后缀”；允许其起点大于 1。

### 4.3 路由算法

给定 `segmentNumber`，路由分两段：

```csharp
if (segmentNumber < recentBaseSegmentNumber) {
    relativePath = MakeArchiveRelativeSegmentPath(segmentNumber);
}
else {
    relativePath = MakeRecentRelativeSegmentPath(segmentNumber);
}
```

内存模型只需两个整数：

- `uint _recentBaseSegmentNumber`
- `int _recentCount`

recent 和 archive 的文件路径都是 segment number 的纯函数，无需存储路径列表。
Commit 热路径上不做 segment 扫描，也不做归档维护。
当前实现只长期持有 active file；历史 segment 句柄不缓存。

### 4.4 轮换触发条件

当 `HasCommittedBranchPointingIntoActiveSegment() && ActiveFile.TailOffset > RotationThreshold` 时，下一次 commit 创建新 segment（编号 = `ActiveSegmentNumber + 1`）。

### 4.5 active / sealed 语义

- sealed segment：不再写入
- active segment：当前唯一写入文件

### 4.6 recent / archive 维护时机

为了避免把“文件整理”耦合进 commit 事务，当前实现采用更保守的职责划分：

- `Commit` 不负责归档旧 segment
- `Repository.MaintainSegmentLayout()` 提供显式的 best-effort 维护入口
- `Repository.Open()` 会在打开 recent window 后尝试做一次 best-effort 维护

因此：

- `recent/` 的目标文件数当前是一个**软目标**，不是事务级硬约束
- `archive/` 的 bucket 路径规则是硬约束

---

## 5. TailMeta 与 commit 身份

`ObjectMap` 帧的 TailMeta 只包含 4 字节：

```text
[0..3]   GraphRoot.LocalId.Value (uint LE)
```

commit 身份完全由 `CommitAddress(uint SegmentNumber, CommitTicket)` 确定：
- `SegmentNumber` 由 branch JSON 记录
- `CommitTicket` 即 ObjectMap 帧的 `SizedPtr` ticket

---

## 6. Commit 流程

以"推进某个 branch"为例，当前语义如下：

```text
Repository.Commit(graphRoot):
  0. 通过 graphRoot.Revision.BranchName O(1) 定位 branch

Repository.CommitCore(branchName, graphRoot):  // internal 入口
  1. 获取该 branch 当前值 oldBranch
  2. 决定目标 segment / 写入方式：
     a. 如需轮换：OpenPendingRotation() → 新 segment 接收全量 SaveAs
     b. 不需轮换：
        - 若 revision.HeadSegmentNumber == ActiveSegmentNumber：原地 Commit 到 active file
        - 否则：SaveAs 到 active file
  3. 调用 Revision.Commit(graphRoot, targetFile)
     或 Revision.SaveAs(graphRoot, targetFile)
  4. 确定 targetSegmentNumber（active 或新 rotated segment 的编号）
  5. 以 CAS 语义原子推进 refs/branches/{branchName}.json：
       expected = oldBranch
       new = { segmentNumber, newTicket }
  6. CAS 成功后：
     - 如有 pending rotation，则提交 active segment 切换
     - 调用 revision.AcceptPersistedSegment(targetSegmentNumber)
     - 更新内存中的 branch 状态 / loaded revision 状态
```

注意：

- durable 顺序上，commit 数据必须先落盘，再推进 branch。
- 如果 branch CAS 失败，该 `Repository` 实例标记为 poisoned，要求 dispose + reopen。
- Revision 方法不再接收 `commitSequence` 参数（Phase B 已消除）。
- `Revision` 不再用 `BoundFile` 做对象身份比较；改为只记录 `HeadSegmentNumber`，由 `Repository` 决定这次传入哪个 file。
- `HeadSegmentNumber` 的推进时机后移到 branch CAS 成功之后；因此"写盘成功"与"branch 前进成功"是两个明确分离的阶段。
- 归档维护不在 commit 事务路径内；commit 成功不依赖 recent 目录整理成功。

### 6.1 Branch 更新使用 CAS

branch 推进建模为：

```text
CompareAndSwapBranchAtomically(branchName, expectedOld, newHead)
```

CAS 失败说明 repo 内存视角与 branch 文件已分叉。新 commit 数据可能已 durable，但 branch 未推进；该实例进入 poisoned 状态。

---

## 7. Open / 恢复流程

### 7.1 Repository.Create

```text
Create(repoDir):
  1. 创建目录结构（幂等：refs/branches/、recent/、archive/）
  2. 获取 state-journal.lock（进程独占）
  3. 在锁内检查目录是否只有 lock 文件
     → 若不是，释放锁并返回错误
  4. 创建初始 segment #1（00000001.sj.rbf）
  5. 构建 recent window 并返回 Repository
```

### 7.2 Repository.Open

```text
Open(repoDir):
  1. 获取 state-journal.lock
  2. 加载 refs/branches/ 目录，建立可写 branch 元数据映射
  3. 扫描 recent/*.sj.rbf 与 archive/**/*.sj.rbf
  4. 校验：
     - segment 编号全局连续、无空洞
     - recent 是最新连续后缀
     - archive 文件路径与 bucket 规则匹配
  5. 从 recent segment 构建 recent window，确定 active segment
  6. best-effort 执行一次 recent → archive 收敛维护
  7. Repository 返回时，不加载任何 Revision（按需 CheckoutBranch）
```

**简洁的恢复语义**：segment 文件和 branch 文件就是全部真相。
当前 `Open()` 不做“修复语义上的损坏”，但会做一次 best-effort 的目录整理；如果 segment 结构损坏，`Open()` 或后续 `CheckoutBranch()` 会报错。

### 7.3 CheckoutBranch

```text
CheckoutBranch(branchName):
  1. 读取 branch 元数据 { segmentNumber, ticket }
  2. 根据 segmentNumber O(1) 找 segment 文件
  3. 若目标是 active segment：复用 active file
  4. 若目标是历史 segment：临时只读打开该 segment
  5. Revision.Open(commitTicket, segmentFile, segmentNumber) 打开工作会话
  6. 历史 segment 的临时句柄在 Open 完成后立即释放
```

---

## 8. 当前实现摘要

当前 [`Repository`](../../src/StateJournal/Repository.cs) 已经落地了下面这些核心能力：

- `refs/branches/*.json` 作为 branch 命名空间
- `recent/*.sj.rbf` 作为共享 segment 的最新连续后缀窗口
- `archive/{bucket}/` 作为旧 segment 的稳定落点
- `CreateBranch()` / `CreateBranch(fromBranch)` / `CheckoutBranch()`
- `ValidateBranchName()`：白名单命名规则 + 路径穿越防护
- repo 级单写锁（`state-journal.lock`，`Create` 先锁后检查，消除竞态窗口）
- active segment 轮换
- recent window 索引（`base + count` 两个整数）：recent 和 archive 路径均由 segment number 直接推导
- `Revision.BranchName`（set-once 反向索引）：`Commit(graphRoot)` O(1) 定位 branch
- `Revision.HeadSegmentNumber`：决定当前 branch 提交时走原地 `Commit` 还是跨 segment `SaveAs`
- branch CAS 推进（基于 `CommitAddress`）
- poisoned repository 语义
- `_maxCommittedSegmentNumber` 缓存：对 rotation 决策 O(1)
- `MaintainSegmentLayout()`：显式 best-effort 归档维护
- `Open()` 时的 best-effort recent 收敛
- 历史 segment 按次只读打开，不做长期句柄缓存

当前仍可继续演进的点主要是：

- 更系统的文档命名统一（例如是否把 `Revision` 进一步表述为 workspace/session）
- recent 目标窗口大小是否应参数化或改成双阈值策略

---

## 9. 当前阶段不做什么

为了降低复杂性，当前阶段明确不做：

- 全局 `CommitTicket → Segment` 索引
- 多进程并发写
- 分布式或内容哈希身份
- 类 Git 的 branch/worktree 限制语义
- 脱离 branch 的历史保留策略

已经做、但刻意保持简单的点：

- 仅做文件级 segment 归档，不做 segment 合并/压缩
- `recent` 文件数只做软收敛，不做事务级硬限制
- 归档维护不耦合到 `Commit`

先把：

- 命名 branch
- 共享 segment（recent window + archive bucket）
- 单写提交
- 按需加载 revision

这四件事做稳，后面空间就很大。

---

## 10. 一句话总结

> `Repository` = 全局锁 + 命名 refs + 编号分段存储 + 按需打开工作会话。

一旦这层站稳，多 branch、轮换、后续 GC，都会变成可组合的局部问题，而不是纠缠在一起的大问题。
