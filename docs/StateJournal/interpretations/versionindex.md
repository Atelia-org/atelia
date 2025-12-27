# VersionIndex 释义（Design Intent）

> 目的：把 atelia/docs/StateJournal/mvp-design-v2.md 中关于 **VersionIndex** 的“规范性意图（Normative Intent）”抽出来，作为实现者的短文档入口。
>
> 范围：仅覆盖 VersionIndex 的角色、持久化身份、与 DurableDict 的关系、以及对实现结构的约束。
>
> 非目标：不讨论 RBF framing 细节；不讨论 GC/Compaction；不提供完整实现伪代码。

---

## 1. 定义（SSOT 摘要）

**VersionIndex**：每个 epoch 的 `ObjectId → ObjectVersionPtr` 映射表。它是 StateJournal 对象图的“引导扇区（boot sector）”，其指针 `VersionIndexPtr` 直接存储在 `MetaCommitRecord` 中，用于打破“先有索引才能找到索引”的死锁。

对应条款与段落（以 mvp-design-v2.md 为准）：

- **[F-VERSIONINDEX-REUSE-DURABLEDICT]**：VersionIndex 复用 DurableDict；key 为 `ObjectId as ulong`，value 使用 `Val_Ptr64` 编码 `ObjectVersionPtr`。
- **[S-VERSIONINDEX-BOOTSTRAP]**：首次 Commit 时 VersionIndex 使用 Well-Known `ObjectId = 0`，写入 `PrevVersionPtr = 0` 的 Genesis Base 版本，并将 `MetaCommitRecord.VersionIndexPtr` 指向它。
- **Well-Known ObjectId / [S-OBJECTID-RESERVED-RANGE]**：`0..15` 为保留区，`0` 预留给 VersionIndex。

---

## 2. 设计意图：VersionIndex 与 DurableDict 的关系

本设计中，“复用 DurableDict”应理解为：

- **持久化形态（on-disk representation）**：VersionIndex 的版本记录就是 `ObjectVersionRecord(ObjectKind=Dict)`，其 `DiffPayload` 采用 DurableDict 的 dict diff 编码（value 选用 `Val_Ptr64`）。
- **对象身份（Object Identity）**：VersionIndex 的 durable 身份固定为 **`ObjectId = 0`**。
- **语义层角色（semantic role）**：所谓“VersionIndex”，是 ObjectId=0 的那份 DurableDict 在系统中的用途/角色，而不是必然要求存在一个对外暴露的、独立的“VersionIndex durable object 类型”。

换句话说：

- DurableDict 是“通用的 durable 容器原语（low-level primitive）”。
- VersionIndex 是“系统约定的一份 well-known DurableDict（ObjectId=0）”，其值域被约束为 `Ptr64`。

---

## 3. 关键推论（对实现结构的约束）

### 3.1 VersionIndex 不应引入第二套 durable 生命周期

如果实现中存在一个 `VersionIndex : IDurableObject` 且它内部再持有一个 DurableDict，那么容易形成“两套 durable 身份/生命周期”的错觉：

- 语义上 VersionIndex 的 durable 身份应当只有一个：`ObjectId=0` 的 dict。
- 额外的 wrapper 只应是 **facade/view**，不应成为“又一个 durable object”。

### 3.2 Workspace 绑定与系统对象的一致性

设计文档在 Workspace 绑定增补（§3.1.2.1）中，把“对象绑定 Owning Workspace”作为 MUST。

因此，任何对外可见/可参与 Lazy Load 的 durable object 实例都不应通过“无 workspace 绑定”的构造路径创建。

对 VersionIndex 的直接含义是：

- ObjectId=0 的 DurableDict 也应当和其它对象一样绑定到 Workspace（即使它是系统对象）。
- 如果实现为了 VersionIndex 引入“不绑定 workspace 的 DurableDict/基类构造函数”，会把一个规范性 MUST 变成“可选建议”，并扩散到其它对象（结构性风险）。

### 3.3 ValueType：VersionIndex 的 value 域必须是 Ptr64

VersionIndex 的 value 在规范里是 `ObjectVersionPtr`（Ptr64 / Address64）。

实现建议（非规范性，但强烈建议）：

- 在类型系统上把 `Ptr64` 与普通 `ulong` 分离（例如 `readonly struct Ptr64`），避免把“用户业务 ulong”误编码为 `Val_Ptr64`。
- 如果 MVP 仍以 `ulong` 承载 `Ptr64`，至少要把“DurableDict 接受 ulong 作为 value”的能力限制在系统路径（避免 silent corruption）。

---

## 4. 建议的实现心智模型（给实现者的短句）

- VersionIndex 是 **ObjectId=0 的 DurableDict**。
- 只有 DurableDict 参与两阶段提交；VersionIndex 只是对该 DurableDict 的“类型化读写视图”。
- `MetaCommitRecord.VersionIndexPtr` 是 boot pointer，永远指向 ObjectId=0 的某个版本记录。

---

## 5. 与实现评审相关的检查点（Checklist）

实现/重构时，可按以下问题做自检：

1. 是否存在“无 Workspace 绑定”的 durable object 实例？如果有，是否只是测试替身？
2. ObjectId=0 的对象是否只有一个 durable 身份（不出现 wrapper 也参与 IDurableObject 生命周期的双重表达）？
3. VersionIndex 的 value 写出时是否必然走 `Val_Ptr64`（而非 `VarInt`/其它）？
4. `LoadObject(0)` 是否明确为内部路径，避免外部把系统对象当普通对象使用？

