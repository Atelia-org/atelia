# StateJournal MVP 实施计划

> **版本**: 0.3  
> **创建**: 2025-12-25  
> **状态**: 已审阅  
> **依据**: [mvp-design-v2.md](mvp-design-v2.md) v3.7 + [T-20251225-01 审计结果](../../agent-team/handoffs/task-result.md)
> **审阅**: [2025-12-25-implementation-plan-review.md](../../agent-team/meeting/2025-12-25-implementation-plan-review.md)

---

## 1. 概述

### 1.1 目标

实现 StateJournal MVP，包括：
- **RBF Layer 0**：崩溃安全的 append-only 帧格式
- **StateJournal Layer 1**：基于 RBF 的持久化对象堆

### 1.2 方法

- **阶段化实施**：按依赖顺序分 5 个 Phase
- **任务粒度**：每个任务 1-4 小时，可独立验收
- **测试驱动**：每个任务对应测试向量/验收标准
- **Agent 协作**：通过 `runSubagent` 调用 Implementer 执行

### 1.3 前置条件

| 依赖 | 状态 | 位置 |
|------|------|------|
| AteliaResult<T> | ✅ 完成 | `atelia/src/Primitives/` |
| AteliaError | ✅ 完成 | `atelia/src/Primitives/` |
| 规范文档 | ✅ 完成 | `atelia/docs/StateJournal/*.md` |

---

## 2. 实施阶段

### Phase 1: RBF Layer 0（底层帧格式）

- **目标**：实现 append-only 帧格式的读写能力
- **预估工时**：8-12h
- **前置依赖**：无
- **输出**：`atelia/src/Rbf/`（独立程序集 `Atelia.Rbf`）
- **测试**：`atelia/tests/Rbf.Tests/`

| 任务 ID | 名称 | 预估 | 条款覆盖 |
|---------|------|------|----------|
| T-P1-01 | Fence/常量定义 | 1h | `[F-FENCE-DEFINITION]`, `[F-GENESIS]` |
| T-P1-02 | Frame 布局与对齐 | 2h | `[F-FRAME-LAYOUT]`, `[F-FRAME-4B-ALIGNMENT]`, `[F-HEADLEN-FORMULA]`, `[F-STATUSLEN-FORMULA]` |
| T-P1-03 | CRC32C 实现 | 1h | `[F-CRC32C-COVERAGE]`, `[F-CRC32C-ALGORITHM]` |
| T-P1-04 | IRbfFramer/Builder | 3h | `[A-RBF-FRAMER-INTERFACE]`, `[A-RBF-FRAME-BUILDER]`, `[S-RBF-BUILDER-*]`, `[F-FRAMESTATUS-*]` |
| T-P1-05 | IRbfScanner/逆向扫描 | 3h | `[A-RBF-SCANNER-INTERFACE]`, `[R-REVERSE-SCAN-ALGORITHM]`, `[R-RESYNC-BEHAVIOR]`, `[S-RBF-TOMBSTONE-VISIBLE]` |

> **注**：MVP 的 HeadLen/TailLen 使用 u32 LE 固定长度编码，非 VarInt。VarInt 仅用于 Payload 内的业务字段。

---

### Phase 2: 核心类型与编码

- **目标**：实现 StateJournal 核心类型和变长编码
- **预估工时**：7-9h
- **前置依赖**：Phase 1（部分任务可并行，见下方标注）
- **输出**：`atelia/src/StateJournal/Core/`

| 任务 ID | 名称 | 预估 | 条款覆盖 | 并行标注 |
|---------|------|------|----------|----------|
| T-P2-00 | 错误类型定义 | 0.5h | StateJournal 所有 `*-REJECT`, `*-FAILFAST` 错误码 | ⚡ 可与 P1 并行 |
| T-P2-01 | Address64/Ptr64 | 1h | `[F-ADDRESS64-*]`, `[F-PTR64-WIRE-FORMAT]` | — |
| T-P2-02 | VarInt 编解码 | 2h | `[F-VARINT-CANONICAL-ENCODING]`, `[F-DECODE-ERROR-FAILFAST]` | ⚡ 可与 P1 并行 |
| T-P2-03 | FrameTag 位段编码 | 2h | `[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]`, `[F-FRAMETAG-SUBTYPE-*]` | — |
| T-P2-04 | DurableObjectState 枚举 | 1h | `[A-OBJECT-STATE-*]`, `[S-STATE-TRANSITION-MATRIX]` | ⚡ 可与 P1 并行 |
| T-P2-05 | IDurableObject 接口 | 2h | `[A-HASCHANGES-O1-COMPLEXITY]`, 基础接口定义 | — |

---

### Phase 3: DurableDict 实现

- **目标**：实现核心容器类型 DurableDict
- **预估工时**：12-16h
- **前置依赖**：Phase 2
- **输出**：`atelia/src/StateJournal/Objects/`

| 任务 ID | 名称 | 预估 | 条款覆盖 |
|---------|------|------|----------|
| T-P3-01 | DiffPayload 格式 | 3h | `[F-KVPAIR-HIGHBITS-RESERVED]`, `[S-DIFF-KEY-SORTED-UNIQUE]`, `[S-PAIRCOUNT-ZERO-LEGALITY]` |
| T-P3-02 | ValueType 编码 | 2h | `[F-UNKNOWN-VALUETYPE-REJECT]`, tombstone/varint/ptr64/objref |
| T-P3-03a | DurableDict 基础结构 | 2h | `[A-DURABLEDICT-API-SIGNATURES]`, `[S-DURABLEDICT-KEY-ULONG-ONLY]`, 双字典 + 基础读写 API |
| T-P3-03b | DurableDict 序列化集成 | 2h | `[S-POSTCOMMIT-WRITE-ISOLATION]`, WritePendingDiff, OnCommitSucceeded |
| T-P3-04 | _dirtyKeys 机制 | 2h | `[S-DIRTYKEYS-TRACKING-EXACT]`, `[S-WORKING-STATE-TOMBSTONE-FREE]` |
| T-P3-05 | DiscardChanges | 2h | `[A-DISCARDCHANGES-REVERT-COMMITTED]`, `[S-DELETE-API-CONSISTENCY]`, `[S-TRANSIENT-DISCARD-DETACH]` |

---

### Phase 4: Workspace 管理

- **目标**：实现对象生命周期管理
- **预估工时**：8-10h
- **前置依赖**：Phase 3
- **输出**：`atelia/src/StateJournal/Workspace/`

| 任务 ID | 名称 | 预估 | 条款覆盖 |
|---------|------|------|----------|
| T-P4-01 | Identity Map | 2h | `[S-IDENTITY-MAP-KEY-COHERENCE]`, WeakReference 机制 |
| T-P4-02 | Dirty Set | 2h | `[S-DIRTYSET-OBJECT-PINNING]`, `[S-DIRTY-OBJECT-GC-PROHIBIT]` |
| T-P4-03 | CreateObject | 2h | `[S-CREATEOBJECT-IMMEDIATE-ALLOC]`, `[S-NEW-OBJECT-AUTO-DIRTY]`, `[S-OBJECTID-RESERVED-RANGE]`, `[S-OBJECTID-MONOTONIC-BOUNDARY]`, `[S-TRANSIENT-DISCARD-OBJECTID-QUARANTINE]` |
| T-P4-04 | LoadObject | 3h | `[A-LOADOBJECT-RETURN-RESULT]`, `[A-OBJREF-TRANSPARENT-LAZY-LOAD]` |
| T-P4-05 | LazyRef<T> | 2h | `[A-OBJREF-BACKFILL-CURRENT]`, 透明加载封装 |

---

### Phase 5: Commit & Recovery

- **目标**：实现提交协议和崩溃恢复
- **预估工时**：12-14h
- **前置依赖**：Phase 4
- **输出**：`atelia/src/StateJournal/Commit/`

| 任务 ID | 名称 | 预估 | 条款覆盖 |
|---------|------|------|----------|
| T-P5-01 | VersionIndex | 3h | `[F-VERSIONINDEX-REUSE-DURABLEDICT]`, `[S-VERSIONINDEX-BOOTSTRAP]` |
| T-P5-02 | MetaCommitRecord | 2h | 格式定义 + 序列化 |
| T-P5-03a | CommitAll 核心流程 | 2h | `[A-COMMITALL-*]`, Dirty Set 遍历、写入 data file |
| T-P5-03b | Commit 完成与恢复 | 2h | `[S-COMMIT-*]`, `[R-COMMIT-FSYNC-ORDER]`, meta 写入、状态清理 |
| T-P5-04 | 崩溃恢复 | 3h | `[R-META-AHEAD-BACKTRACK]`, `[R-DATATAIL-TRUNCATE-*]`, `[R-ALLOCATOR-SEED-FROM-HEAD]` |

---

## 3. 任务清单（完整）

| 任务 ID | Phase | 名称 | 预估 | 依赖 | 验收标准 |
|---------|-------|------|------|------|----------|
| T-P1-01 | 1 | Fence/常量定义 | 1h | — | `Fence == 0x31464252` 且 `FenceBytes.SequenceEqual([0x52,0x42,0x46,0x31])` |
| T-P1-02 | 1 | Frame 布局与对齐 | 2h | T-P1-01 | RBF-LEN-001/002 通过 |
| T-P1-03 | 1 | CRC32C 实现 | 1h | — | CRC 测试向量通过（System.IO.Hashing.Crc32C 对齐） |
| T-P1-04 | 1 | IRbfFramer/Builder | 3h | T-P1-02, T-P1-03 | RBF-SINGLE-001 通过; Auto-abort 测试通过; Single-open 约束测试通过 |
| T-P1-05 | 1 | IRbfScanner/逆向扫描 | 3h | T-P1-04 | RBF-DOUBLE-001 通过; RESYNC 测试通过; Tombstone 帧可见 |
| T-P2-00 | 2 | 错误类型定义 | 0.5h | — | `StateJournalError` 继承 `AteliaError`; 所有错误码已定义 |
| T-P2-01 | 2 | Address64/Ptr64 | 1h | — | 对齐测试：`value % 4 == 0`; 空值测试：`Address64.Null.Value == 0` |
| T-P2-02 | 2 | VarInt 编解码 | 2h | — | Canonical 编码测试通过; 非 canonical 输入 reject |
| T-P2-03 | 2 | FrameTag 位段编码 | 2h | T-P2-01 | FRAMETAG-OK-* 通过 |
| T-P2-04 | 2 | DurableObjectState 枚举 | 1h | — | 枚举值 4 个：Clean/PersistentDirty/TransientDirty/Detached |
| T-P2-05 | 2 | IDurableObject 接口 | 2h | T-P2-04 | 存在至少一个 test double 实现通过编译 |
| T-P3-01 | 3 | DiffPayload 格式 | 3h | T-P2-02 | DICT-OK-001/002 通过 |
| T-P3-02 | 3 | ValueType 编码 | 2h | T-P3-01 | ValueType 边界测试通过; 未知类型 reject |
| T-P3-03a | 3 | DurableDict 基础结构 | 2h | T-P2-05, T-P3-02 | API 签名匹配规范; Set/Get/Remove 基础测试通过 |
| T-P3-03b | 3 | DurableDict 序列化集成 | 2h | T-P3-03a | WritePendingDiff 生成正确 payload; OnCommitSucceeded 追平状态 |
| T-P3-04 | 3 | _dirtyKeys 机制 | 2h | T-P3-03b | DIRTY-001/002/003 通过; `HasChanges == (_dirtyKeys.Count > 0)` |
| T-P3-05 | 3 | DiscardChanges | 2h | T-P3-04 | Persistent 对象重置测试通过; Transient 对象 Detach 测试通过 |
| T-P4-01 | 4 | Identity Map | 2h | T-P3-05 | 同一 ObjectId 加载两次返回相同实例（`ReferenceEquals`） |
| T-P4-02 | 4 | Dirty Set | 2h | T-P4-01 | Dirty 对象调用 `GC.Collect()` 后仍可访问 |
| T-P4-03 | 4 | CreateObject | 2h | T-P4-02 | FIRST-COMMIT-002/003 通过; ObjectId >= 16 |
| T-P4-04 | 4 | LoadObject | 3h | T-P4-03 | 返回 `AteliaResult<T>`; NotFound 时返回 Failure |
| T-P4-05 | 4 | LazyRef<T> | 2h | T-P4-04 | 透明加载测试通过; 回填后不重复加载 |
| T-P5-01 | 5 | VersionIndex | 3h | T-P3-03b | VersionIndex 类型为 DurableDict; Key 为 ulong |
| T-P5-02 | 5 | MetaCommitRecord | 2h | T-P5-01 | 序列化往返测试通过 |
| T-P5-03a | 5 | CommitAll 核心流程 | 2h | T-P4-04, T-P5-02 | Dirty 对象全部写入 data file |
| T-P5-03b | 5 | Commit 完成与恢复 | 2h | T-P5-03a | COMMIT-ALL-001/002 通过; 失败时内存状态不变 |
| T-P5-04 | 5 | 崩溃恢复 | 3h | T-P5-03b | Recovery 测试通过; 撕裂提交正确回退 |

**总计**：27 个任务，约 48-64 小时

---

## 4. runSubagent 调用模板

### 4.1 T-P1-01: Fence/常量定义

```yaml
# @Implementer
taskId: "T-P1-01"
phase: 1
name: "RBF Fence 与常量定义"

targetFiles:
  - "atelia/src/Rbf/RbfConstants.cs"

specFiles:
  - "atelia/docs/StateJournal/rbf-format.md#2-fence"

conditions:
  - "[F-FENCE-DEFINITION]: Fence = 0x31464252 ('RBF1' in ASCII, little-endian)"
  - "[F-GENESIS]: 空文件以单个 Fence 开始"

implementation:
  - "定义 public static class RbfConstants"
  - "Fence: uint = 0x31464252"
  - "FenceBytes: ReadOnlySpan<byte> = [0x52, 0x42, 0x46, 0x31]"

dependencies: []  # 无依赖

testFile: "atelia/tests/Rbf.Tests/RbfConstantsTests.cs"

acceptanceCriteria:
  - "Fence == 0x31464252"
  - "FenceBytes.SequenceEqual([0x52, 0x42, 0x46, 0x31])"
```

---

### 4.2 T-P1-04: IRbfFramer/Builder

```yaml
# @Implementer
taskId: "T-P1-04"
phase: 1
name: "RBF Framer 与 Builder 实现"

targetFiles:
  - "atelia/src/Rbf/IRbfFramer.cs"
  - "atelia/src/Rbf/RbfFrameBuilder.cs"
  - "atelia/src/Rbf/RbfFramer.cs"

specFiles:
  - "atelia/docs/StateJournal/rbf-interface.md#3-写入端"
  - "atelia/docs/StateJournal/rbf-format.md#3-frame-结构"

conditions:
  - "[A-RBF-FRAMER-INTERFACE]: IRbfFramer 接口定义"
  - "[A-RBF-FRAME-BUILDER]: RbfFrameBuilder using 模式"
  - "[S-RBF-BUILDER-AUTO-ABORT]: Dispose 未 Complete 时自动 Abort"
  - "[S-RBF-BUILDER-SINGLE-OPEN]: 同时只能有一个 Builder"
  - "[S-RBF-FRAMER-NO-FSYNC]: Flush 不含 Fsync"

implementation:
  - |
    public interface IRbfFramer {
        RbfFrameBuilder BeginFrame(FrameTag tag);
        void Flush();
        void Fsync();
    }
  - "RbfFrameBuilder: IDisposable, 写入 Payload 后 Complete() 提交"
  - "计算 HeadLen/TailLen, 填充 FrameStatus, 计算 CRC32C"
  - "写入顺序: HeadLen → FrameTag → Payload → FrameStatus → TailLen → CRC32C → Fence"

dependencies:
  - "T-P1-02: Frame 布局"
  - "T-P1-03: CRC32C"

testFile: "atelia/tests/Rbf.Tests/RbfFramerTests.cs"

acceptanceCriteria:
  - "RBF-SINGLE-001 通过"
  - "Auto-abort 测试通过"
  - "Single-open 约束测试通过"
```

---

### 4.3 T-P3-03: DurableDict 双字典实现

```yaml
# @Implementer
taskId: "T-P3-03"
phase: 3
name: "DurableDict 双字典实现"

targetFiles:
  - "atelia/src/StateJournal/Objects/DurableDict.cs"

specFiles:
  - "atelia/docs/StateJournal/mvp-design-v2.md#343-durabledict-双字典策略"
  - "atelia/docs/StateJournal/mvp-design-v2.md#342-dict-的-diffpayload"

conditions:
  - "[A-DURABLEDICT-API-SIGNATURES]: API 签名匹配规范"
  - "[S-DURABLEDICT-KEY-ULONG-ONLY]: Key 限 ulong"
  - "[S-WORKING-STATE-TOMBSTONE-FREE]: Working State 无墓碑"

implementation:
  - |
    public class DurableDict : IDurableObject {
        private Dictionary<ulong, object?> _committed;  // Committed State
        private Dictionary<ulong, object?> _current;    // Working State
        private HashSet<ulong> _dirtyKeys;              // 变更追踪
        
        // API
        public void Set(ulong key, object? value);
        public bool TryGetValue(ulong key, out object? value);
        public bool ContainsKey(ulong key);
        public void Delete(ulong key);  // 从 _current 移除, 加入 _dirtyKeys
    }
  - "实现 IDurableObject 接口 (State, HasChanges)"
  - "Delete 在 _current 中移除 key（不存 tombstone）"
  - "序列化时根据 _dirtyKeys 生成 DiffPayload"

dependencies:
  - "T-P2-05: IDurableObject 接口"
  - "T-P3-02: ValueType 编码"

testFile: "atelia/tests/StateJournal.Tests/Objects/DurableDictTests.cs"

acceptanceCriteria:
  - "API 签名匹配"
  - "DICT-OK-001/002/003/004 通过"
  - "_dirtyKeys 精确性测试通过"
```

---

## 5. 质量门禁

| 阶段 | 门禁条件 | 测试向量 |
|------|----------|----------|
| Phase 1 完成 | RBF 读写测试 100% 通过 | rbf-test-vectors.md |
| Phase 2 完成 | 核心类型测试 100% 通过 | FRAMETAG-OK-*, VarInt tests |
| Phase 3 完成 | DurableDict 测试 100% 通过 | DICT-*, DIRTY-* |
| Phase 4 完成 | Workspace 测试 100% 通过 | FIRST-COMMIT-*, LoadObject tests |
| Phase 5 完成 | 端到端 Commit/Recovery 通过 | COMMIT-ALL-*, Recovery tests |
| **MVP 完成** | 全部测试通过 + 集成测试 | 完整测试套件 |

---

## 6. 项目结构（预期）

### 6.1 程序集依赖图

```
Atelia.Primitives  ←── Atelia.Data  ←── Atelia.Rbf  ←── Atelia.StateJournal
                                         (Layer 0)       (Layer 1)
```

### 6.2 目录结构

```
atelia/src/
├── Rbf/                              # Atelia.Rbf (独立程序集, Phase 1)
│   ├── Rbf.csproj
│   ├── RbfConstants.cs
│   ├── IRbfFramer.cs
│   ├── RbfFramer.cs
│   ├── RbfFrameBuilder.cs
│   ├── IRbfScanner.cs
│   ├── RbfScanner.cs
│   └── RbfFrame.cs
└── StateJournal/                     # Atelia.StateJournal (Phase 2-5)
    ├── StateJournal.csproj
    ├── Core/                         # Phase 2
    │   ├── Address64.cs
    │   ├── VarInt.cs
    │   ├── FrameTag.cs
    │   ├── DurableObjectState.cs
    │   └── IDurableObject.cs
    ├── Objects/                      # Phase 3
    │   ├── DurableDict.cs
    │   ├── DiffPayload.cs
    │   └── ValueType.cs
    ├── Workspace/                    # Phase 4
    │   ├── IdentityMap.cs
    │   ├── DirtySet.cs
    │   ├── StateJournalWorkspace.cs
    │   └── LazyRef.cs
    └── Commit/                       # Phase 5
        ├── VersionIndex.cs
        ├── MetaCommitRecord.cs
        ├── CommitEngine.cs
        └── RecoveryEngine.cs

atelia/tests/
├── Rbf.Tests/                        # Atelia.Rbf.Tests
│   └── Rbf.Tests.csproj
└── StateJournal.Tests/               # Atelia.StateJournal.Tests
    └── StateJournal.Tests.csproj
```

---

## 附录 A: 条款-任务映射

> 反向索引：从条款查找对应任务

| 条款 ID | 任务 ID |
|---------|---------|
| `[F-FENCE-DEFINITION]` | T-P1-01 |
| `[F-GENESIS]` | T-P1-01 |
| `[F-FRAME-LAYOUT]` | T-P1-02 |
| `[F-STATUSLEN-FORMULA]` | T-P1-02 |
| `[F-CRC32C-*]` | T-P1-03 |
| `[A-RBF-FRAMER-INTERFACE]` | T-P1-04 |
| `[A-RBF-FRAME-BUILDER]` | T-P1-04 |
| `[F-FRAMESTATUS-*]` | T-P1-04 |
| `[A-RBF-SCANNER-INTERFACE]` | T-P1-05 |
| `[R-REVERSE-SCAN-ALGORITHM]` | T-P1-05 |
| `[S-RBF-TOMBSTONE-VISIBLE]` | T-P1-05 |
| `*-REJECT`, `*-FAILFAST` 错误码 | T-P2-00 |
| `[F-ADDRESS64-*]` | T-P2-01 |
| `[F-VARINT-CANONICAL-ENCODING]` | T-P2-02 |
| `[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]` | T-P2-03 |
| `[A-OBJECT-STATE-*]` | T-P2-04 |
| `[S-STATE-TRANSITION-MATRIX]` | T-P2-04 |
| `[S-DIFF-KEY-SORTED-UNIQUE]` | T-P3-01 |
| `[F-KVPAIR-HIGHBITS-RESERVED]` | T-P3-01 |
| `[F-UNKNOWN-VALUETYPE-REJECT]` | T-P3-02 |
| `[A-DURABLEDICT-API-SIGNATURES]` | T-P3-03a |
| `[S-POSTCOMMIT-WRITE-ISOLATION]` | T-P3-03b |
| `[S-DIRTYKEYS-TRACKING-EXACT]` | T-P3-04 |
| `[A-DISCARDCHANGES-REVERT-COMMITTED]` | T-P3-05 |
| `[S-TRANSIENT-DISCARD-DETACH]` | T-P3-05 |
| `[S-IDENTITY-MAP-KEY-COHERENCE]` | T-P4-01 |
| `[S-DIRTYSET-OBJECT-PINNING]` | T-P4-02 |
| `[S-CREATEOBJECT-IMMEDIATE-ALLOC]` | T-P4-03 |
| `[S-OBJECTID-MONOTONIC-BOUNDARY]` | T-P4-03 |
| `[S-TRANSIENT-DISCARD-OBJECTID-QUARANTINE]` | T-P4-03 |
| `[A-LOADOBJECT-RETURN-RESULT]` | T-P4-04 |
| `[A-OBJREF-TRANSPARENT-LAZY-LOAD]` | T-P4-04 |
| `[A-OBJREF-BACKFILL-CURRENT]` | T-P4-05 |
| `[F-VERSIONINDEX-REUSE-DURABLEDICT]` | T-P5-01 |
| `[A-COMMITALL-*]` | T-P5-03a |
| `[S-COMMIT-*]` | T-P5-03b |
| `[R-META-AHEAD-BACKTRACK]` | T-P5-04 |
| `[R-DATATAIL-TRUNCATE-*]` | T-P5-04 |

---

## 变更日志

| 版本 | 日期 | 变更 |
|------|------|------|
| 0.3 | 2025-12-25 | **架构决策**：RBF 独立为 `Atelia.Rbf` 程序集（编译时强制单向依赖）; 更新项目结构和模板路径 |
| 0.2 | 2025-12-25 | **畅谈会审阅**：新增 T-P2-00（错误类型）; 拆分 T-P3-03→03a/03b, T-P5-03→03a/03b; 补充遗漏条款映射; 具体化验收标准; 标注可并行任务; 总任务数 24→27 |
| 0.1 | 2025-12-25 | 初始草案，基于 T-20251225-01 审计结果 |
