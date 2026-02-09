# L1 审阅任务包：Commit 模块

> **briefId**: L1-Commit-2025-12-26-001
> **reviewType**: L1
> **createdBy**: Team Leader
> **createdAt**: 2025-12-26

---

## 🎯 焦点

**模块**：`atelia/src/StateJournal/Commit/`

**specRef**:
- commit: HEAD (main branch)
- files:
  - `atelia/docs/StateJournal/mvp-design-v2.md` — §3.4.5 (CommitAll), §3.5 (崩溃恢复), §META-COMMIT-RECORD

---

## 📋 条款清单

### Group H: MetaCommitRecord

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[F-META-COMMIT-RECORD]` | Payload 布局 | EpochSeq/RootObjectId/VersionIndexPtr/DataTail/NextObjectId |
| MetaCommitRecord 恢复 | TryRead 错误处理 | 字段截断时返回错误 |

**规范原文摘要**：

> `MetaCommitRecord` payload：
> - `EpochSeq`：`varuint` — 单调递增
> - `RootObjectId`：`varuint`
> - `VersionIndexPtr`：`u64 LE`
> - `DataTail`：`u64 LE`
> - `NextObjectId`：`varuint`

### Group I: VersionIndex

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[F-VERSIONINDEX-REUSE-DURABLEDICT]` | 复用 DurableDict | key=ObjectId, value=Val_Ptr64 |
| `[S-VERSIONINDEX-BOOTSTRAP]` | 引导扇区初始化 | 首次 Commit 使用 ObjectId=0 |
| `[S-OBJECTID-RESERVED-RANGE]` | ObjectId 保留区 | 0..15 保留 |

**规范原文摘要**：

> **[F-VERSIONINDEX-REUSE-DURABLEDICT]** MVP 中 VersionIndex 复用 DurableDict（key 为 ObjectId as ulong，value 使用 Val_Ptr64 编码 ObjectVersionPtr）

> **[S-VERSIONINDEX-BOOTSTRAP]** 首次 Commit 时，VersionIndex 使用 Well-Known ObjectId = 0

### Group J: Commit 语义

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[R-COMMIT-FSYNC-ORDER]` | 刷盘顺序 | data fsync → meta fsync |
| `[R-COMMIT-POINT-META-FSYNC]` | Commit Point 定义 | meta fsync 完成时刻 |
| `[S-HEAP-COMMIT-FAIL-INTACT]` | Commit 失败不改内存 | 全局不变式 |
| `[S-COMMIT-FAIL-RETRYABLE]` | 可重试 | 失败后可重新 Commit |
| `[A-COMMITALL-FLUSH-DIRTYSET]` | CommitAll() | 提交所有 Dirty 对象 |

**规范原文摘要**：

> **[R-COMMIT-FSYNC-ORDER]** 先 fsync data，再 fsync meta

> **[R-COMMIT-POINT-META-FSYNC]** Commit Point 是 meta fsync 完成时刻

> **[S-HEAP-COMMIT-FAIL-INTACT]** 若 CommitAll 返回失败，所有对象的内存状态 MUST 保持调用前不变

> **[S-COMMIT-FAIL-RETRYABLE]** 调用方可以在失败后再次调用 CommitAll，不需要手动清理状态

> **[A-COMMITALL-FLUSH-DIRTYSET]** CommitAll()：保持当前 root 不变，提交 Dirty Set 中的所有对象

### Group K: 恢复

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[R-META-AHEAD-BACKTRACK]` | meta 领先处理 | 继续回扫上一条 |
| `[R-DATATAIL-TRUNCATE-GARBAGE]` | 截断垃圾 | 以 DataTail 截断 |
| `[R-ALLOCATOR-SEED-FROM-HEAD]` | Allocator 初始化 | 仅从 HEAD 获取 |

**规范原文摘要**：

> **[R-META-AHEAD-BACKTRACK]** 若发现"meta 记录有效但指针不可解引用/越界"，按"撕裂提交"处理：继续回扫上一条 meta 记录

> **[R-DATATAIL-TRUNCATE-GARBAGE]** 以该 record 的 DataTail 截断 data 文件尾部垃圾

> **[R-ALLOCATOR-SEED-FROM-HEAD]** Allocator 初始化 MUST 仅从 HEAD 的 NextObjectId 字段获取；MUST NOT 通过扫描 data 文件推断更大 ID

---

## 🔍 代码入口

| 文件 | 职责 | 条款关联 |
|:-----|:-----|:---------|
| `Commit/MetaCommitRecord.cs` | 元提交记录 | F-META-COMMIT-RECORD |
| `Commit/VersionIndex.cs` | 版本索引 | F-VERSIONINDEX-REUSE-DURABLEDICT, S-VERSIONINDEX-BOOTSTRAP |
| `Commit/CommitContext.cs` | 提交上下文 | A-COMMITALL-* |
| `Commit/RecoveryInfo.cs` | 恢复信息 | R-META-AHEAD-BACKTRACK, R-ALLOCATOR-SEED-FROM-HEAD |
| `Commit/WorkspaceRecovery.cs` | 工作空间恢复 | R-META-AHEAD-BACKTRACK, R-DATATAIL-TRUNCATE-GARBAGE |

**相关测试**：
- `Commit/MetaCommitRecordTests.cs`
- `Commit/VersionIndexTests.cs`
- `Commit/CommitContextTests.cs`
- `Commit/WorkspaceRecoveryTests.cs`

---

## 📚 依赖上下文

**前置条款**（来自 Core）：
- VarInt 编解码
- IDurableObject 接口

**前置条款**（来自 Objects）：
- DurableDict 实现（VersionIndex 复用）

**前置条款**（来自 Workspace）：
- Workspace.PrepareCommit, FinalizeCommit

---

## 📋 审阅指令

**角色**：L1 符合性法官

### MUST DO

1. 逐条款检查代码是否满足规范语义
2. 每个 Finding 必须引用：条款原文 + 代码位置 + 复现方式
3. 遇到规范未覆盖的行为 → 标记为 `U`（Underspecified），不是 `V`

### MUST NOT

1. 不评论代码风格（那是 L3）
2. 不假设规范未写的约束
3. 不产出无法复现的 Finding

### 特别关注

- **MetaCommitRecord Payload 布局**：检查字段顺序和编码是否与规范一致
- **VersionIndex.WellKnownObjectId**：确认为 0
- **Recovery 回扫逻辑**：检查 DataTail > actualDataSize 时是否继续回扫
- **RecoveryInfo.Empty**：确认 NextObjectId = 16
- **Commit 两阶段**：PrepareCommit 不改内存，FinalizeCommit 改内存

---

## 📤 输出格式

**文件**：`atelia/docs/StateJournal/review/L1-Commit-2025-12-26-findings.md`

**格式**：EVA-v1（参见 Recipe）

---

## ⚠️ 注意事项

1. **MVP 阶段**：CommitContext 是模拟实现，不含实际 I/O。审阅时关注逻辑正确性而非实际存储。

2. **fsync 顺序**：规范要求 data fsync → meta fsync，但 MVP 无实际 I/O，检查 Workspace.PrepareCommit/FinalizeCommit 的调用顺序是否符合语义。

3. **VersionIndex 值类型**：规范说 value 使用 Val_Ptr64，检查 DurableDict<ulong?> 的序列化是否正确处理 ulong。
