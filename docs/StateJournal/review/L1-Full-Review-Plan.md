# StateJournal MVP L1 全量审阅计划

> **创建日期**：2025-12-26
> **负责人**：刘德智 (Team Leader)
> **状态**：执行中

---

## 📋 审阅目标

对 StateJournal MVP 实现进行 L1 符合性审阅，验证代码是否忠实实现规范条款。

**规范文档**：
- `atelia/docs/StateJournal/mvp-design-v2.md`（v3.7）
- `atelia/docs/StateJournal/rbf-interface.md`（v0.10）

**代码范围**：
- `atelia/src/StateJournal/` — 4 个模块，~20 个文件
- `atelia/tests/StateJournal.Tests/` — 对应测试文件

---

## 📊 模块分解

| 模块 | 文件数 | 条款组 | 依赖 | 状态 |
|:-----|:------:|:-------|:-----|:----:|
| **Core** | 7 | VarInt, Ptr64, FrameTag, Errors, IDurableObject, State | 无 | ✅ 完成 |
| **Objects** | 3 | ValueType, DiffPayload, DurableDict | Core | ⏳ 待审 |
| **Workspace** | 4 | IdentityMap, DirtySet, LazyRef, Workspace | Core, Objects | ⏳ 待审 |
| **Commit** | 5 | MetaCommitRecord, VersionIndex, CommitContext, Recovery | Core, Objects, Workspace | ⏳ 待审 |

---

## 📑 详细条款清单

### Phase 1: Core 模块 ✅ DONE

> 审阅结果：17/17 Conform，0 Violation

详见 [L1-Core-2025-12-26-findings.md](L1-Core-2025-12-26-findings.md)

---

### Phase 2: Objects 模块

**文件**：
- `Objects/ValueType.cs` — ValueType 枚举
- `Objects/DiffPayload.cs` — DiffPayload 编解码
- `Objects/DurableDict.cs` — DurableDict 实现

**条款清单**：

#### Group A: ValueType

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[F-KVPAIR-HIGHBITS-RESERVED]` | 高 4 位预留 | 低 4 bit = ValueType，高 4 bit MUST 写 0，reader 见非 0 视为格式错误 |
| `[F-UNKNOWN-VALUETYPE-REJECT]` | 未知 ValueType | 低 4 bit 不在 {0,1,2,3,4} MUST fail-fast |

#### Group B: DiffPayload 格式

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[S-DIFF-KEY-SORTED-UNIQUE]` | Key 唯一升序 | 单个 diff 内 key MUST 严格唯一且升序 |
| `[S-PAIRCOUNT-ZERO-LEGALITY]` | PairCount=0 合法性 | 仅 Base Version (PrevVersionPtr=0) 允许 |
| `[S-OVERLAY-DIFF-NONEMPTY]` | Overlay diff 非空 | writer MUST NOT 为无变更对象写版本 |

#### Group C: DurableDict 不变式

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[S-WORKING-STATE-TOMBSTONE-FREE]` | Working State 纯净 | tombstone 不得出现在可枚举视图 |
| `[S-DELETE-API-CONSISTENCY]` | Delete 一致性 | ContainsKey/TryGetValue/Enumerate 必须一致 |
| `[S-COMMIT-FAIL-MEMORY-INTACT]` | Commit 失败不改内存 | 失败后内存状态保持不变 |
| `[S-COMMIT-SUCCESS-STATE-SYNC]` | Commit 成功追平 | CommittedState == CurrentState |
| `[S-POSTCOMMIT-WRITE-ISOLATION]` | 隔离性 | 后续写入不影响 _committed |
| `[S-DIFF-CANONICAL-NO-NETZERO]` | Canonical Diff | 不含 net-zero 变更 |
| `[S-DIFF-REPLAY-DETERMINISM]` | 可重放性 | Apply(S, D) == CurrentState |
| `[S-DIRTYKEYS-TRACKING-EXACT]` | _dirtyKeys 精确性 | 精确追踪变更 |
| `[A-DISCARDCHANGES-REVERT-COMMITTED]` | DiscardChanges | 重置为 _committed 副本 |
| `[S-DURABLEDICT-KEY-ULONG-ONLY]` | Key 类型 | key 固定为 ulong |

#### Group D: DurableDict API

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[A-DURABLEDICT-API-SIGNATURES]` | API 签名 | TryGetValue 返回 Result，Remove 返回 bool |
| `[A-OBJREF-TRANSPARENT-LAZY-LOAD]` | 透明 Lazy Load | 读取 ObjRef 时自动 LoadObject |
| `[A-OBJREF-BACKFILL-CURRENT]` | 回填 _current | Lazy Load 后回填实例 |

---

### Phase 3: Workspace 模块

**文件**：
- `Workspace/IdentityMap.cs` — ObjectId → WeakRef 映射
- `Workspace/DirtySet.cs` — Dirty 对象强引用集合
- `Workspace/LazyRef.cs` — 延迟加载引用
- `Workspace/Workspace.cs` — Workspace API

**条款清单**：

#### Group E: Identity Map & Dirty Set

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[S-DIRTYSET-OBJECT-PINNING]` | Dirty Set 强引用 | MUST 持有强引用直到 Commit 成功 |
| `[S-IDENTITY-MAP-KEY-COHERENCE]` | Key 一致性 | key 等于对象 ObjectId |
| `[S-DIRTY-OBJECT-GC-PROHIBIT]` | Dirty 对象不被 GC | 由强引用保证 |
| `[S-NEW-OBJECT-AUTO-DIRTY]` | 新建对象自动 Dirty | CreateObject 后立即加入 Dirty Set |
| `[S-STATE-TRANSITION-MATRIX]` | 状态转换矩阵 | 遵循规范定义的转换规则 |

#### Group F: LazyRef

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| LazyRef 错误类型 | 错误定义 | NotInitialized, NoWorkspace, InvalidStorage |

#### Group G: Workspace API

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[A-LOADOBJECT-RETURN-RESULT]` | LoadObject 返回 Result | 不返回 null 或抛异常 |
| `[S-CREATEOBJECT-IMMEDIATE-ALLOC]` | CreateObject 立即分配 | 立即分配 ObjectId |
| `[S-TRANSIENT-DISCARD-OBJECTID-QUARANTINE]` | ObjectId 隔离 | 进程内不重用 |

---

### Phase 4: Commit 模块

**文件**：
- `Commit/MetaCommitRecord.cs` — 元提交记录
- `Commit/VersionIndex.cs` — 版本索引
- `Commit/CommitContext.cs` — 提交上下文
- `Commit/RecoveryInfo.cs` — 恢复信息
- `Commit/WorkspaceRecovery.cs` — 工作空间恢复

**条款清单**：

#### Group H: MetaCommitRecord

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| MetaCommitRecord payload | Payload 布局 | EpochSeq/RootObjectId/VersionIndexPtr/DataTail/NextObjectId |

#### Group I: VersionIndex

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[F-VERSIONINDEX-REUSE-DURABLEDICT]` | 复用 DurableDict | key=ObjectId, value=Val_Ptr64 |
| `[S-VERSIONINDEX-BOOTSTRAP]` | 引导扇区初始化 | 首次 Commit 使用 ObjectId=0 |
| `[S-OBJECTID-RESERVED-RANGE]` | ObjectId 保留区 | 0..15 保留 |

#### Group J: Commit 语义

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[R-COMMIT-FSYNC-ORDER]` | 刷盘顺序 | data fsync → meta fsync |
| `[R-COMMIT-POINT-META-FSYNC]` | Commit Point 定义 | meta fsync 完成时刻 |
| `[S-HEAP-COMMIT-FAIL-INTACT]` | Commit 失败不改内存 | 全局不变式 |
| `[S-COMMIT-FAIL-RETRYABLE]` | 可重试 | 失败后可重新 Commit |
| `[A-COMMITALL-FLUSH-DIRTYSET]` | CommitAll() | 提交所有 Dirty 对象 |

#### Group K: 恢复

| ID | 标题 | 要点 |
|:---|:-----|:-----|
| `[R-META-AHEAD-BACKTRACK]` | meta 领先处理 | 继续回扫上一条 |
| `[R-DATATAIL-TRUNCATE-GARBAGE]` | 截断垃圾 | 以 DataTail 截断 |
| `[R-ALLOCATOR-SEED-FROM-HEAD]` | Allocator 初始化 | 仅从 HEAD 获取 |

---

## 📈 进度追踪

| Phase | 模块 | 开始时间 | 完成时间 | 结果 | Findings |
|:-----:|:-----|:---------|:---------|:-----|:---------|
| 1 | Core | 2025-12-26 11:00 | 2025-12-26 11:30 | ✅ 17C/0V/0U | [findings](L1-Core-2025-12-26-findings.md) |
| 2 | Objects | 2025-12-26 12:00 | 2025-12-26 12:30 | ⚠️ 11C/2V/3U | [findings](L1-Objects-2025-12-26-findings.md) |
| 3 | Workspace | 2025-12-26 13:00 | 2025-12-26 13:30 | ✅ 12C/0V/1U | [findings](L1-Workspace-2025-12-26-findings.md) |
| 4 | Commit | 2025-12-26 14:00 | 2025-12-26 14:30 | ✅ 14C/0V/0U | [findings](L1-Commit-2025-12-26-findings.md) |

**全量审阅完成时间**: 2025-12-26 14:30

---

## 📤 产出物

每个 Phase 产出：
1. **Mission Brief**: `L1-{Module}-2025-12-26-brief.md`
2. **Findings**: `L1-{Module}-2025-12-26-findings.md`

最终产出：
- **汇总报告**: `L1-Full-Review-Summary.md`

---

## 🔧 执行方法

1. 为每个模块准备 Mission Brief
2. 调用 CodexReviewer SubAgent 执行审阅
3. 收集 Findings 到对应文件
4. 更新本计划的进度追踪
5. 完成后生成汇总报告

---

## ⚠️ 风险与应对

| 风险 | 应对 |
|:-----|:-----|
| 上下文长度限制 | 每个 Phase 独立执行，Brief 包含所需上下文 |
| 规范歧义 | 标记为 U（Underspecified），不作为 V 处理 |
| 代码问题 | 记录到 Findings，汇总后统一分析 |

---

> **下一步**：执行 Phase 2 (Objects 模块) 审阅
