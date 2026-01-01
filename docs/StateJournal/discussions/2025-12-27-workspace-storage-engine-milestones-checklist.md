# Workspace 内置存储引擎（移除 ObjectLoaderDelegate）：里程碑施工任务书（可执行清单，交付 Implementer）

日期：2025-12-27
状态：🟡 待实施

目标：将当前 Workspace 从“依赖注入 ObjectLoaderDelegate + 内存模拟 CommitContext”的 MVP 形态，重构为“Workspace 内部持有 {meta,data} 两个 RBF 文件与序列化/反序列化机制”的内置存储引擎形态；最终用户只需传入目标文件夹即可打开/创建仓库，并从 `RootObject` 拿到 `DurableObjectBase` 实例。

范围：atelia/src/StateJournal + atelia/src/Rbf（必要时包含 atelia/src/Data 的 buffer writer 基础设施），以及对应 tests。

不考虑兼容：允许破坏性调整 API/类型/测试。

---

## A. 设计锚点（SSOT / 接口边界）

- RBF 层接口契约（Layer 0/1 边界）：
  - [atelia/docs/StateJournal/rbf-interface.md](../rbf-interface.md)
- StateJournal 规格（Layer 1 SSOT）：
  - [atelia/docs/StateJournal/mvp-design-v2.md](../mvp-design-v2.md)
- FrameTag 位段解释（现有实现）：
  - [atelia/src/StateJournal/Core/StateJournalFrameTag.cs](../../src/StateJournal/Core/StateJournalFrameTag.cs)
- MetaCommitRecord 编解码（现有实现）：
  - [atelia/src/StateJournal/Commit/MetaCommitRecord.cs](../../src/StateJournal/Commit/MetaCommitRecord.cs)

---

## B. 核心决策（本任务必须维持的结构约束）

1. Workspace 的 Core Load 路径不再依赖注入 loader；最终将删除 `ObjectLoaderDelegate`。
2. Commit Point 语义以 meta file 为准：data → durable flush(data) → meta → durable flush(meta) → FinalizeCommit。
3. `ObjectVersionRecord` 的 payload 语义必须严格遵循 SSOT：`PrevVersionPtr(u64 LE) + DiffPayload`。
4. Materialize 必须沿 Version Chain（PrevVersionPtr 链）得到 Committed State。
5. 仍保留 `VersionIndex = ObjectId=0 的 DurableDict` 作为 boot sector（版本索引）。

---

## C. 交付形态（最终用户体验）

- `Workspace.Open(folder)`：
  - 若 folder 中无仓库文件：创建空仓库
  - 若存在：执行 recovery，恢复到 HEAD
- `Workspace.RootObject`：
  - 返回 `AteliaResult<DurableObjectBase>` 或等价 try-pattern
  - 空仓库时不抛异常（返回可诊断错误或 Empty 状态）

---

## D. 里程碑拆分（粗粒度，可并行派工）

> 每个里程碑都必须给出：文件列表（预期改动范围）+ DoD（可判定验收口径）。

### M1. RBF 文件后端：让 IRbfFramer/IRbfScanner 能对真实文件工作

**目标**：从“内存 RbfFramer/RbfScanner”升级到“文件 {Append,ReadAt,ScanReverse,ReadPayload}”。

**主要文件**：
- 既有接口/实现：
  - [atelia/src/Rbf/IRbfFramer.cs](../../src/Rbf/IRbfFramer.cs)
  - [atelia/src/Rbf/IRbfScanner.cs](../../src/Rbf/IRbfScanner.cs)
  - [atelia/src/Rbf/RbfFramer.cs](../../src/Rbf/RbfFramer.cs)
  - [atelia/src/Rbf/RbfScanner.cs](../../src/Rbf/RbfScanner.cs)
- 可能需要复用的写入基础设施：
  - [atelia/src/Data/IReservableBufferWriter.cs](../../src/Data/IReservableBufferWriter.cs)
  - [atelia/src/Data/ChunkedReservableWriter.cs](../../src/Data/ChunkedReservableWriter.cs)

**实施要点**：
- 实现一个可用于 `IBufferWriter<byte>` 的文件追加 writer（或等价抽象），并能提供：
  - 当前 Position（用于 Address64）
  - Flush（推送到 OS）
  - Durable flush（对外暴露给上层，满足 data→meta 的持久化顺序）
- scanner 侧至少满足：TryReadAt + ScanReverse + ReadPayload，并能基于文件内容工作。

**DoD**：
- 能在磁盘上创建一个 .rbf 文件，Append 若干帧后，ScanReverse 能读回相同数量的 Valid 帧。
- `TryReadAt(Address64)` 对有效地址可稳定读取并通过 CRC 校验。

---

### M2. StateJournal Record Reader/Writer：把 FrameTag+Payload 解释成 Meta/ObjectVersion 记录

**目标**：建立 Layer 1 “Record Reader/Writer”，让上层能把 RBF 帧解释为业务记录（MetaCommitRecord / ObjectVersionRecord）。

**主要文件**：
- MetaCommitRecord 编解码：
  - [atelia/src/StateJournal/Commit/MetaCommitRecord.cs](../../src/StateJournal/Commit/MetaCommitRecord.cs)
- FrameTag 解释：
  - [atelia/src/StateJournal/Core/StateJournalFrameTag.cs](../../src/StateJournal/Core/StateJournalFrameTag.cs)

**建议新增文件（示意，按代码组织习惯落地）**：
- `atelia/src/StateJournal/Storage/StateJournalRecordReader.cs`
- `atelia/src/StateJournal/Storage/StateJournalRecordWriter.cs`

**实施要点**：
- MetaCommitRecord：
  - 写：FrameTag=MetaCommit，payload=MetaCommitRecordSerializer.Write
  - 读：payload=MetaCommitRecordSerializer.TryRead
- ObjectVersionRecord：
  - 读 frameTag → RecordType/ObjectKind
  - 校验 payload >= 8（PrevVersionPtr）
  - 切出 DiffPayload 并交由对象类型对应的 diff 解码器

**DoD**：
- 能写出一条 MetaCommitRecord 帧并读回同值。
- 能读取一条 DictVersion 的 ObjectVersionRecord 并解析出 PrevVersionPtr 与 DiffPayload span。

---

### M3. Workspace.Open(folder) + Recovery：从 meta/data 恢复 HEAD

**目标**：实现“只传目录就能打开/恢复到 HEAD”的 Workspace 构造流程。

**主要文件**：
- Workspace（现有）：
  - [atelia/src/StateJournal/Workspace/Workspace.cs](../../src/StateJournal/Workspace/Workspace.cs)
- Recovery 逻辑（现有雏形）：
  - [atelia/src/StateJournal/Commit/WorkspaceRecovery.cs](../../src/StateJournal/Commit/WorkspaceRecovery.cs)
  - [atelia/src/StateJournal/Commit/RecoveryInfo.cs](../../src/StateJournal/Commit/RecoveryInfo.cs)

**实施要点**：
- 定义/固定仓库目录结构（meta/data 文件命名）。
- meta 扫描：ScanReverse 找到最后一条有效 MetaCommitRecord（跳过 Tombstone）。
- DataTail 验证与截断：若 meta 领先 data，继续回扫；若 data > DataTail，truncate 到 DataTail。
- 打开后 Workspace 必须具备：EpochSeq/NextObjectId/VersionIndexPtr/DataTail/RootObjectId。

**DoD**：
- 空目录 Open → 创建空仓库（meta/data 文件存在，且可再次 Open）。
- 有提交的仓库 Open → 恢复到最后一次有效 MetaCommitRecord。
- 能处理 meta 领先 data 的情况：回扫到上一条，并把 data 截断到安全边界。

---

### M4. Materialize 引擎（Dict-only MVP）：沿 VersionChain 生成 Committed State

**目标**：实现 Dict 的 committed state 物化：从 `ObjectVersionPtr` 出发沿 PrevVersionPtr 链回放 diff。

**主要文件**：
- DiffPayloadReader/Writer（现有）：
  - [atelia/src/StateJournal/Objects/DiffPayload.cs](../../src/StateJournal/Objects/DiffPayload.cs)
- DurableDict（现有 committed 构造器已具备）：
  - [atelia/src/StateJournal/Objects/DurableDict.cs](../../src/StateJournal/Objects/DurableDict.cs)

**建议新增文件（示意）**：
- `atelia/src/StateJournal/Materialization/DictMaterializer.cs`

**实施要点**：
- 输入：ObjectVersionPtr（Address64/Ptr64）
- 循环：ReadAt(ptr) → parse ObjectVersionRecord → push(diff) → ptr=PrevVersionPtr，直到 0
- apply 顺序：base → overlay（逆序应用）
- tombstone 语义：Remove key（Working State tombstone-free）
- ObjRef 值：保留为 ObjectId（以支持透明 Lazy Load / backfill 语义）

**DoD**：
- 对同一 objectId 连续提交 N 次后，重开仓库 materialize 的 committed dict 等于最后一次提交的 committed 状态。
- 对 payload 校验失败（payload<8、unknown ValueType、key delta 非法等）能 fail-fast 并返回可诊断错误。

---

### M5. Commit 切换到真实 RBF meta/data（替换 CommitContext）

**目标**：让 Commit 变成真正的 I/O：写 data 帧、durable flush(data)、写 meta 帧、durable flush(meta)、FinalizeCommit。

**主要文件**：
- Workspace 的提交逻辑（现有，需替换 CommitContext）：
  - [atelia/src/StateJournal/Workspace/Workspace.cs](../../src/StateJournal/Workspace/Workspace.cs)
- MetaCommitRecord（写 meta 需要）：
  - [atelia/src/StateJournal/Commit/MetaCommitRecord.cs](../../src/StateJournal/Commit/MetaCommitRecord.cs)

**实施要点**：
- data：对 dirty objects 写 ObjectVersionRecord（FrameTag=DictVersion），得到 Address64 更新 VersionIndex。
- versionIndex：ObjectId=0 的 DurableDict 如变更也写入 data，并更新 VersionIndexPtr。
- meta：写 MetaCommitRecord（EpochSeq+RootObjectId+VersionIndexPtr+DataTail+NextObjectId）。
- durable flush 顺序：data 再 meta。

**DoD**：
- `Commit()` 后，仓库可被 `Open(folder)` 恢复到该 commit。
- 断电/崩溃模拟（至少通过“截断 data/meta”构造）可被 recovery 回扫修复到最后有效 commit point。

---

### M6. 移除 ObjectLoaderDelegate：LoadObject 只走 VersionIndex+Materialize

**目标**：删除注入式 loader，Workspace 内部完全自洽。

**主要文件**：
- Workspace：
  - [atelia/src/StateJournal/Workspace/Workspace.cs](../../src/StateJournal/Workspace/Workspace.cs)

**实施要点**：
- 删除 `ObjectLoaderDelegate`、`_objectLoader` 字段及相关构造函数分支。
- `LoadObject(objectId)`：IdentityMap miss → VersionIndex 查 ptr → materialize → `new DurableDict(this, objectId, committed)` → cache。

**DoD**：
- 全仓库编译无 `ObjectLoaderDelegate` 相关引用。
- 所有测试通过，并新增至少 1 个端到端 test 覆盖“open folder → load object”。

---

### M7. 端到端集成测试：锁住 Open/Commit/Recovery/RootObject

**目标**：用集成测试锁住“仓库可恢复、root 可读、版本链可回放”的核心承诺。

**建议新增测试文件（示意）**：
- `atelia/tests/StateJournal.Tests/Storage/WorkspaceStorageRoundtripTests.cs`

**必须覆盖的用例**：
1. roundtrip：Create root dict → Commit → Dispose → Open(folder) → RootObject committed state 正确。
2. version chain：同一对象多次 Commit → 重开后 state 等于 HEAD。
3. recovery/backtrack：meta 领先 data（DataTail > actual）→ Open 能回扫并截断。

**DoD**：
- `dotnet test` 全绿。
- 上述 3 类用例均有自动化覆盖。

---

## E. 并行派工建议（你可直接按角色分配）

- Implementer A：M1（RBF 文件后端 I/O）
- Implementer B：M4（Materialize Dict-only）
- Implementer C：M5 + M6（Commit 切换 + 删除 loader）
- QA：M7（集成测试）

---

## F. 风险提示（核查视角，务必提前条款化）

1. **“对象存在性”与空 diff**：新建对象若无变更可能不生成 ObjectVersionRecord；必须明确 object existence 的最小写出策略（否则恢复后 VersionIndex/Load 语义会漂移）。
2. **DoD 必须可判定**：每个里程碑都要能写测试或至少能通过 deterministic 的文件扫描验证。
3. **未知 kind/valueType 的处理**：规范要求 fail-fast；未来支持自定义 kind 时需以 runtime registry 为判定基准（不可用“编译时 enum 未列出”作为 unknown 的唯一标准）。
