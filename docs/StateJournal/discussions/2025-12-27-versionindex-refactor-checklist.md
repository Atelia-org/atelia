# VersionIndex 重构清单（可执行，交付 Implementer）

日期：2025-12-27
状态：**✅ 已完成** (2025-12-27)

目标：让实现结构与 mvp-design-v2.md 的"VersionIndex = ObjectId=0 的 DurableDict（语义角色）"一致；消除"VersionIndex 是另一种 durable object/另一套 DurableDict"的结构性歧义；同时移除"无 Workspace 绑定的 durable object"通道。

范围：atelia/src/StateJournal 下的 VersionIndex/Workspace/DurableDict/DurableObjectBase 及相关测试。

不考虑兼容：允许破坏性调整 API/类型/测试（按用户指令）。

---

## 完成摘要

- **代码改动**：4 个核心文件
- **测试改动**：6 个测试文件
- **测试结果**：601/601 通过 ✅

### 核心代码改动

| 文件 | 改动 |
|------|------|
| [VersionIndex.cs](../../src/StateJournal/Commit/VersionIndex.cs) | 移除 `IDurableObject` 实现，改为 DurableDict 的类型化视图 |
| [Workspace.cs](../../src/StateJournal/Workspace/Workspace.cs) | 持有 `_versionIndexDict` + `_versionIndex` 视图 |
| [DurableObjectBase.cs](../../src/StateJournal/Objects/DurableObjectBase.cs) | 移除"无 workspace 绑定"构造函数 |
| [DurableDict.cs](../../src/StateJournal/Objects/DurableDict.cs) | 移除"无 workspace 绑定"构造函数；添加 `NotifyOwnerDirty()` 方法 |

### 测试改动

| 文件 | 改动 |
|------|------|
| VersionIndexTests.cs | 完全重写：25 个直接测试 → 15 个集成测试 |
| DurableDictTests.cs | 替换所有构造函数调用（194 处） |
| DirtySetTests.cs | 替换所有构造函数调用（38 处） |
| IdentityMapTests.cs | 替换所有构造函数调用（40 处） |
| LazyRefTests.cs | 替换所有构造函数调用（24 处） |
| WorkspaceTests.cs | 替换所有构造函数调用（6 处） |

### 发现并修复的 Bug

- **Clean→Dirty 状态转换未同步 DirtySet**：DurableDict 从 Clean 修改为 Dirty 时，需要通知 Workspace 将其添加到 DirtySet

---

## A. 规范锚点（实现必须满足）

- mvp-design-v2.md：
  - [F-VERSIONINDEX-REUSE-DURABLEDICT]
  - [S-VERSIONINDEX-BOOTSTRAP]
  - [S-OBJECTID-RESERVED-RANGE]
  - §3.1.2.1 Workspace 绑定（S-WORKSPACE-OWNING-EXACTLY-ONE / S-WORKSPACE-CTOR-REQUIRES-WORKSPACE / S-LAZYLOAD-DISPATCH-BY-OWNER）

---

## B. 目标结构（建议方案：VersionIndex 退化为 facade）

### B1. 新结构摘要

- Durable 身份：ObjectId=0 的对象就是 `DurableDict`。
- `VersionIndex` 类型（若保留）不再实现 `IDurableObject`，只作为 **view/facade**：提供 `TryGetObjectVersionPtr/SetObjectVersionPtr/ObjectIds/Count` 等类型化 API。
- Workspace 持有并提交的对象是 `_versionIndexDict : DurableDict`（ObjectId=0）。

### B2. 关键不变量

- 不允许任何 `IDurableObject` 实例“无 workspace 绑定”创建（除非明确限定为纯测试替身，且不参与 Lazy Load）。
- Commit 时 VersionIndex 的写出仍然是 `ObjectVersionRecord(ObjectKind=Dict)`，value 必须用 `Val_Ptr64`。

---

## C. 文件级改动点（按优先级）

### C1. 版本索引：改为 view，不再是 durable object

- 目标文件：atelia/src/StateJournal/Commit/VersionIndex.cs
  - 变更：
    - 移除 `: IDurableObject` 实现与 Object 生命周期成员（ObjectId/State/HasChanges/WritePendingDiff/OnCommitSucceeded/DiscardChanges）。
    - 将该类型改为 `sealed class VersionIndexView`（或保留名 `VersionIndex` 但语义为 view），构造函数接收 `DurableDict`（必须是 ObjectId=0 的那份）。
    - 对外 API 保留：
      - `TryGetObjectVersionPtr(ulong objectId, out ulong versionPtr)`：内部 `dict.TryGetValue(objectId, out object?)` 并断言类型为 `ulong`（或未来 `Ptr64`）。
      - `SetObjectVersionPtr(ulong objectId, ulong versionPtr)`：内部 `dict.Set(objectId, versionPtr)`。
      - `ObjectIds/Count/ComputeNextObjectId`：委托给 dict。
    - 明确：view 不应暴露 `IDurableObject` 语义。

> 备注：如果希望最大化减少改名波及，也可以保留类名 VersionIndex，但它应是“view”，不要再实现 IDurableObject。

### C2. Workspace：持有 well-known DurableDict（ObjectId=0）并提交它

- 目标文件：atelia/src/StateJournal/Workspace/Workspace.cs
  - 字段调整：
    - `private readonly VersionIndex _versionIndex;` → `private readonly DurableDict _versionIndexDict;`
    - 可选：新增只读 view：`public VersionIndexView VersionIndex { get; }`
  - 构造函数调整：
    - 在 Workspace 构造时创建 `_versionIndexDict`：
      - 使用 **绑定 workspace** 的构造函数（建议以 Clean 空 committed 初始化）：`new DurableDict(this, 0, new Dictionary<ulong, object?>())`
      - 禁止使用任何“无 workspace 绑定”的构造路径。
  - PrepareCommit 调整：
    - 原先写入 VersionIndex 的分支仍保留，但对象改为 `_versionIndexDict`。
    - 更新映射时使用 `_versionIndexDict.Set(objectId, position)`。
    - `TryGetVersionPtr` 改为从 `_versionIndexDict` 读取。
  - FinalizeCommit 调整：
    - ` _versionIndex.OnCommitSucceeded();` → `_versionIndexDict.OnCommitSucceeded();`

### C3. DurableDict：移除“无 workspace 绑定”构造函数

- 目标文件：atelia/src/StateJournal/Objects/DurableDict.cs
  - 删除/禁用：
    - `internal DurableDict(ulong objectId)`
    - `internal DurableDict(ulong objectId, Dictionary<ulong, object?> committed)`
  - 影响：VersionIndex 不再能通过无绑定路径创建 DurableDict；系统对象必须也绑定 Workspace。

### C4. DurableObjectBase：移除“无 workspace 绑定”构造函数（或至少收窄）

- 目标文件：atelia/src/StateJournal/Objects/DurableObjectBase.cs
  - 删除/禁用：
    - `private protected DurableObjectBase(ulong objectId, DurableObjectState initialState)`
  - 或（次优但可接受的过渡）：
    - 保留但加上强约束：仅供测试/极少内部类型，且这些类型不得触发 Lazy Load（并把这条限制写成代码级 guard）。

---

## D. 测试调整（必须同步）

### D1. VersionIndexTests：改为通过 Workspace + view 测试 ✅ DONE

- 目标文件：atelia/tests/StateJournal.Tests/Commit/VersionIndexTests.cs
  - 完成时间：2025-12-27
  - 变更摘要：
    - **删除的测试**（25 个）：
      - 所有直接测试 `new VersionIndex()` 构造函数的测试
      - 所有测试 `IDurableObject` 状态的测试（State/HasChanges/WritePendingDiff/OnCommitSucceeded/DiscardChanges）
      - 包括：`SetAndGet_ObjectVersionPtr`、`TryGet_NonExistent_ReturnsFalse`、`SetMultipleTimes_OverwritesPreviousValue`、`ObjectIds_ReturnsAllSetKeys`、`Count_ReturnsCorrectNumber`、`ComputeNextObjectId_*` 等直接测试 VersionIndex 实例的测试
      - `VersionIndex_IsTransientDirty_WhenNew`、`VersionIndex_IsClean_WhenLoadedFromCommitted`、`VersionIndex_HasChanges_AfterSet`、`VersionIndex_NoChanges_WhenNewAndUnmodified`、`VersionIndex_Clean_AfterSet_BecomesPersistentDirty`
      - `VersionIndex_WritePendingDiff_*` 系列
      - `VersionIndex_OnCommitSucceeded_*` 系列
      - `VersionIndex_DiscardChanges_*` 系列
      - `VersionIndex_FromCommitted_*` 系列
      - `TwoPhaseCommit_RoundTrip`、`AfterCommit_Modify_BecomesPersistentDirty`
    - **保留/新增的测试**（15 个）：
      - `VersionIndex_WellKnownObjectId_IsZero` — 验证常量值
      - `Workspace_CreateObjectAndCommit_VersionIndexRecordsObject` — 集成测试
      - `Workspace_BeforeCommit_TryGetVersionPtrReturnsFalse` — 集成测试
      - `Workspace_MultipleObjects_AllRecordedInVersionIndex` — 集成测试
      - `Workspace_TryGetVersionPtr_NonExistentObjectId_ReturnsFalse` — 集成测试
      - `Workspace_MultipleCommits_VersionPtrUpdates` — 集成测试
      - `Workspace_CleanObject_NotIncludedInCommit` — 集成测试
      - `Workspace_FirstCommit_SetsVersionIndexPtr` — 集成测试
      - `Workspace_MultipleCommits_VersionIndexPtrUpdates` — 集成测试
      - `Workspace_EmptyCommit_VersionIndexPtrUnchanged` — 集成测试
      - `Workspace_NewWorkspace_NextObjectIdIs16` — 验证 ComputeNextObjectId 语义
      - `Workspace_CreateObject_NextObjectIdIncrements` — 验证 ComputeNextObjectId 语义
      - `Workspace_ObjectIds_AfterReservedRange` — 验证保留区保护
      - `Workspace_PrepareCommit_ReturnsValidContext` — 验证 CommitContext
      - `Workspace_FinalizeCommit_UpdatesState` — 验证二阶段提交
  - 测试策略：
    - **旧策略**：直接测试 VersionIndex 的 IDurableObject 行为
    - **新策略**：通过 Workspace 测试 VersionIndex 的集成行为
    - IDurableObject 状态行为现在由底层的 DurableDict(ObjectId=0) 负责，在 DurableDictTests 中测试
  - 测试状态：编译通过（VersionIndexTests.cs 无编译错误）

### D2. DirtySetTests：使用 TestHelper 创建 DurableDict ✅ DONE

- 目标文件：atelia/tests/StateJournal.Tests/Workspace/DirtySetTests.cs
  - 完成时间：2025-12-27
  - 变更摘要：
    - 添加 `using static Atelia.StateJournal.Tests.TestHelper;`
    - 将所有 `new DurableDict(xxx)` 替换为 `CreateDurableDict()` / `CreateMultipleDurableDict(n)` 模式
    - 辅助方法改为返回 ObjectId（不再硬编码）
    - 所有测试改为使用 `obj.ObjectId` 而非硬编码的 ID 值
  - 测试状态：编译通过（依赖的其他测试文件仍有错误，但 DirtySetTests.cs 本身无错误）

### D2.5. IdentityMapTests：使用 TestHelper 创建 DurableDict ✅ DONE

- 目标文件：atelia/tests/StateJournal.Tests/Workspace/IdentityMapTests.cs
  - 完成时间：2025-12-27
  - 变更摘要：
    - 添加 `using static Atelia.StateJournal.Tests.TestHelper;`
    - 将所有 `new DurableDict(xxx)` 替换为 `CreateDurableDict()` / `CreateMultipleDurableDict(n)` 模式
    - 修改 `AddObjectAndReleaseReference` 辅助方法返回 `ulong` ObjectId（不再接收硬编码参数）
    - 所有测试改为使用动态 ObjectId 而非硬编码值
    - 更新 `Add_DuplicateObjectId_ThrowsInvalidOperationException` 测试语义（现测试不同对象共存）
    - 为所有创建了 Workspace 的测试添加 `GC.KeepAlive(ws)` 确保对象生命周期
  - 测试状态：编译通过，18/18 测试全部通过

### D3. 其它引用点

- 搜索并更新所有 `VersionIndex` 的构造与使用点（目前主要集中在 Workspace 与测试）。

---

## E. 风险与决策点（实现时必须显式选择）

1) 是否允许外部 `LoadObject(0)`：
   - 建议：禁止/内部化，避免系统对象被用户当普通对象使用。

2) Ptr64 的类型化：
   - MVP 可以先用 `ulong` 但必须限制 DurableDict 的 `ulong` value 写入路径（只允许系统用途），避免把用户业务 `ulong` 误编码为 `Val_Ptr64`。

3) 空仓库行为：
   - 允许内存里始终存在 ObjectId=0 的 dict，但在首次 commit 前它不应被当成“已存在于 VersionIndex 中的对象”（避免语义污染）。

---

## F. 验收（Definition of Done）

- 代码层面：
  - 无任何 DurableObject 可以在不传 Workspace 的情况下被构造（除测试替身外）。
  - Workspace 的 VersionIndex 写入路径只涉及 DurableDict（ObjectId=0）。
  - `dotnet test` 全绿。

- 语义层面：
  - VersionIndex 的 durable 身份唯一：ObjectId=0 的 dict。
  - `MetaCommitRecord.VersionIndexPtr` 的生产与消费链路清晰（boot pointer）。

