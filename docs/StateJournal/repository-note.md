# Repository 专项笔记

> 日期：2026-03-20
> 状态：已与当前 `Repository` 实现对齐（CommitSequence 已消除）

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
现在这一切已被消除。每个 commit 的天然地址是独立的 `CommitAddress(uint SegmentNumber, CommitId)`——就像 `{楼号, 房间号}` 天然唯一。

消除后的收益：
- `sequence.json` 文件不再存在
- `Open()` 恢复流程从"三路 recovery"简化为"扫描 segment + 加载 branch"
- Segment 路由从 O(log N) 二分查找变为 O(1) 直接索引
- TailMeta 从 12 字节缩减为 4 字节（仅需存 GraphRoot LocalId）

---

## 1. 核心模型

### 1.1 Repository

`Repository` 是 repo 级协调者，负责：

- 进程独占锁（`state-journal.lock`）
- `refs/branches/` 的发现、加载与原子更新
- active segment 的选择与轮换
- 已加载 `Revision` 实例的生命周期管理

### 1.2 Revision

`Revision` 表示：

- 从某个已提交 commit 打开的内存对象图
- 可编辑、可 commit
- 持有自己的 `_head` 视角
- 持有 `BranchName`（internal set-once 属性），由 `Repository` 在绑定时设置一次

因此它更接近"工作会话 / checkout / workspace"，而不是"commit 本身"。

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
    00000001.sj.rbf              # 文件名 = SegmentNumber（uint, 1-based hex8）
    00000002.sj.rbf
    00000003.sj.rbf
```

说明：

- `refs/` 是总命名空间根。
- `refs/branches/` 是当前唯一由 `Repository` 读写的 movable branch 层。
- `refs/tags/` 预留给未来只读标签；当前阶段不创建、不扫描。
- `recent/` 是共享的 segment 集合，不属于某个单独 branch。

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

刻意不保存 segment 文件路径，因为 `segmentNumber → 文件` 是简单的 O(1) 直接索引。

---

## 4. Segment 命名与路由

### 4.1 基本规则

每个 `.sj.rbf` 文件名等于该 segment 的编号（`uint`，1-based hex8）：

```text
00000001.sj.rbf    # segment #1
00000002.sj.rbf    # segment #2
```

### 4.2 必须成立的不变量

- 全 repo 任意时刻只有一个 active segment（最后一个）。
- active segment 接收所有 branch 的新 commit。
- 一个 commit 只写入一个 segment。
- 新 segment 一旦启用，后续所有 commit 都只进入这个新 segment。

### 4.3 路由算法

给定 `segmentNumber`，O(1) 直接通过内存 `_index` 数组定位文件：

```csharp
var idx = (int)(segmentNumber - 1); // 1-based → 0-based
var relativePath = _index[idx].RelativePath;
```

`_index` 在 `Create` / `Open` 时从磁盘一次性构建，后续 segment 轮换时追加更新。Commit 热路径上**不访问磁盘**。

### 4.4 轮换触发条件

当 `HasCommittedBranchPointingIntoActiveSegment() && ActiveFile.TailOffset > RotationThreshold` 时，下一次 commit 创建新 segment（编号 = `_index.Count + 1`）。

### 4.5 active / sealed 语义

- sealed segment：不再写入
- active segment：当前唯一写入文件

---

## 5. TailMeta 与 commit 身份

`ObjectMap` 帧的 TailMeta 只包含 4 字节：

```text
[0..3]   GraphRoot.LocalId.Value (uint LE)
```

commit 身份完全由 `CommitAddress(uint SegmentNumber, CommitId)` 确定：
- `SegmentNumber` 由 branch JSON 记录
- `CommitId` 即 ObjectMap 帧的 `SizedPtr` ticket

---

## 6. Commit 流程

以"推进某个 branch"为例，当前语义如下：

```text
Repository.Commit(graphRoot):
  0. 通过 graphRoot.Revision.BranchName O(1) 定位 branch

Repository.CommitCore(branchName, graphRoot):  // internal 入口
  1. 获取该 branch 当前值 oldBranch
  2. 决定目标 segment：
     a. 如需轮换：OpenPendingRotation() → 新 segment 接收全量 SaveAs
     b. 已在 active segment：原地 Commit
     c. 不在 active segment：SaveAs 到 active segment
  3. 调用 Revision.Commit(graphRoot) 或 Revision.SaveAs(graphRoot, targetFile)
  4. 确定 targetSegmentNumber（active 或新 rotated segment 的编号）
  5. 以 CAS 语义原子推进 refs/branches/{branchName}.json：
       expected = oldBranch
       new = { segmentNumber, newTicket }
  6. 更新内存中的 branch 状态 / loaded revision 状态
```

注意：

- durable 顺序上，commit 数据必须先落盘，再推进 branch。
- 如果 branch CAS 失败，该 `Repository` 实例标记为 poisoned，要求 dispose + reopen。
- Revision 方法不再接收 `commitSequence` 参数（Phase B 已消除）。

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
  1. 创建目录结构（幂等：refs/branches/、recent/）
  2. 获取 state-journal.lock（进程独占）
  3. 在锁内检查目录是否只有 lock 文件
     → 若不是，释放锁并返回错误
  4. 创建初始 segment #1（0000000000000001.sj.rbf）
  5. 构建 _index 并返回 Repository
```

### 7.2 Repository.Open

```text
Open(repoDir):
  1. 获取 state-journal.lock
  2. 加载 refs/branches/ 目录，建立可写 branch 元数据映射
  3. 一次性扫描 recent/*.sj.rbf，构建 segment 列表
  4. 从 segment 列表构建 _index，确定 active segment
  5. Repository 返回时，不加载任何 Revision（按需 CheckoutBranch）
```

**简洁的恢复语义**：不需要任何 recovery — segment 文件和 branch 文件就是全部真相。
`Open()` 没有 repair 逻辑、没有 fallback。如果 segment 结构损坏，在 `CheckoutBranch` 时 `Revision.Open` 会检测并报错。

### 7.3 CheckoutBranch

```text
CheckoutBranch(branchName):
  1. 读取 branch 元数据 { segmentNumber, ticket }
  2. 根据 segmentNumber O(1) 找 segment 文件
  3. Revision.Open(commitId, segmentFile) 打开工作会话
```

---

## 8. 当前实现摘要

当前 [`Repository`](../../src/StateJournal/Repository.cs) 已经落地了下面这些核心能力：

- `refs/branches/*.json` 作为 branch 命名空间
- `recent/*.sj.rbf` 作为共享 segment 集合（uint 1-based 编号，hex8 命名）
- `CreateBranch()` / `CreateBranch(fromBranch)` / `CheckoutBranch()`
- `ValidateBranchName()`：白名单命名规则 + 路径穿越防护
- repo 级单写锁（`state-journal.lock`，`Create` 先锁后检查，消除竞态窗口）
- active segment 轮换
- 内存 segment 索引（`_index`）：O(1) 直接索引，Commit 热路径无磁盘扫描
- `Revision.BranchName`（set-once 反向索引）：`Commit(graphRoot)` O(1) 定位 branch
- branch CAS 推进（基于 `CommitAddress`）
- poisoned repository 语义
- `_maxCommittedSegmentNumber` 缓存：对 rotation 决策 O(1)

当前仍可继续演进的点主要是：

- segment 归档 / 文件级 GC
- 更系统的文档命名统一（例如是否把 `Revision` 进一步表述为 workspace/session）

---

## 9. 当前阶段不做什么

为了降低复杂性，当前阶段明确不做：

- 全局 `CommitId → Segment` 索引
- segment 归档与压缩
- 多进程并发写
- 分布式或内容哈希身份
- 类 Git 的 branch/worktree 限制语义
- 脱离 branch 的历史保留策略

先把：

- 命名 branch
- 共享 segment（按 uint 编号直接索引）
- 单写提交
- 按需加载 revision

这四件事做稳，后面空间就很大。

---

## 10. 一句话总结

> `Repository` = 全局锁 + 命名 refs + 编号分段存储 + 按需打开工作会话。

一旦这层站稳，多 branch、轮换、后续 GC，都会变成可组合的局部问题，而不是纠缠在一起的大问题。
