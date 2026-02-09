# ObjectVersionRecord 与 VersionIndex

> **来源**：从 `mvp-design-v2.md` §3.2.3 / §3.2.4 / §3.2.5 提取。
> 本文档是 ObjectVersionRecord 布局、VersionIndex 存储结构和 Commit Record 逻辑概念的 SSOT（唯一权威来源）。
>
> **依赖**：
> - DiffPayload 编码格式见 [dict-delta.md](dict-delta.md)
> - ValueType / varint 定义见 [primitive-serialization.md](primitive-serialization.md)

---

## ObjectVersionRecord（对象版本，增量 DiffPayload）

每个对象的版本以链式版本组织：

- `PrevVersionPtr`：Ptr64（该 ObjectId 的上一个版本；若为 0 表示 **Base Version**（Genesis Base 或 Checkpoint Base））
- `ObjectKind`：由 FrameTag 高 16 位提供（参见 `[F-OBJVER-OBJECTKIND-FROM-TAG]`），用于选择 `DiffPayload` 解码器
  - **[F-UNKNOWN-OBJECTKIND-REJECT]** 遇到未知 Kind 必须抛出异常（Fail-fast）。
- `DiffPayload`：依对象类型而定（本 MVP 至少要求：`Dict` 与 `VersionIndex` 可工作）

### data payload 布局

> **FrameTag RecordType = 0x0001** 表示 ObjectVersionRecord；**SubType** 表示 ObjectKind（参见枚举值速查表）。
> Payload 从业务字段开始，**不再包含 ObjectKind 字节**（已移至 FrameTag）。

- `PrevVersionPtr`：`u64 LE`（Ptr64：byte offset；`0=null`，且 4B 对齐）
- `DiffPayload`：bytes（直接从 `payload[8]` 开始）

**`[F-OBJVER-PAYLOAD-MINLEN]`** ObjectVersionRecord payload MUST 至少 8 字节（`PrevVersionPtr`）。若不足，Reader MUST 视为格式错误。

---

## VersionIndex（ObjectId → ObjectVersionPtr 的映射对象）

> **Bootstrap 入口（引导扇区）**：VersionIndex 是整个对象图的**引导扇区（Boot Sector）**。
> 它的指针（`VersionIndexPtr`）直接存储在 `MetaCommitRecord` 中，无需通过 VersionIndex 自身查询。
> 这打破了"读 VersionIndex 需要先查 VersionIndex"的概念死锁。
> VersionIndex 使用 Well-Known ObjectId `0`（参见术语表 **Well-Known ObjectId** 条目）。

VersionIndex 是一个 durable object，它自身也以版本链方式存储。

**[F-VERSIONINDEX-REUSE-DURABLEDICT]** VersionIndex 落地选择：

- VersionIndex 复用 `DurableDict`（key 为 `ObjectId` as `ulong`，value 使用 `Val_Ptr64` 编码 `ObjectVersionPtr`）。
- 因此，VersionIndex 的版本记录本质上是 `ObjectVersionRecord(ObjectKind=Dict)`，其 `DiffPayload` 采用 [dict-delta.md](dict-delta.md) 的 dict diff 编码。

### 更新方式

- 每个 epoch 写一个"覆盖表"版本：只包含本次 commit 中发生变化的 ObjectId 映射。
- 查找允许链式回溯：先查 HEAD overlay，miss 再沿 `PrevVersionPtr` 回溯。

### MVP 约束与默认策略

- MVP 允许通过 **Checkpoint Base** 作为主要链长控制手段：写入 `PrevVersionPtr=0` 的"全量 state"版本，以封顶 replay 与回溯成本。
  - **[S-CHECKPOINT-HISTORY-CUTOFF]** Checkpoint Base 标志着版本链的起点，无法回溯到更早历史（断链）。
  - **[S-MSB-HACK-REJECTED]** MVP 明确否决使用 MSB Hack（如 `PrevVersionPtr` 最高位）来维持历史链；若需完整历史追溯，应由外部归档机制负责。
- 另外，为避免 **任何 `DurableDict`（包括 VersionIndex）** 的版本链在频繁 commit 下无上限增长，MVP **建议实现一个通用的 Checkpoint Base 触发规则（可关闭）**：
	- 当某个 dict 对象的版本链长度超过 `DictCheckpointEveryNVersions`（默认建议：`64`）时，下一次写入该对象新版本时写入一个 **Checkpoint Base**：
		- `PrevVersionPtr = 0`
		- `DiffPayload` 写入"完整表"（等价于从空 dict apply 后得到当前全量 state）
	- 若未实现该规则，则链长完全依赖手动/文件级控制。

---

## Commit Record（逻辑概念）

在采用 meta file 的方案中，"Commit Record"在物理上等价于 `MetaCommitRecord`（commit log 的一条）。本文仍保留 Commit Record 的逻辑概念，用于描述版本历史。

Commit Record（逻辑上）至少包含：

- `EpochSeq`：单调递增
- `RootObjectId`：ObjectId（`varuint` 编码）
- `VersionIndexPtr`：Ptr64（指向一个"VersionIndex durable object"的版本）
- `DataTail`：Ptr64
- `CRC32C`（物理上由 `RBF` framing 提供，不在 payload 内重复存储）

> 说明：MVP 不提供"按 parent 指针遍历历史"的能力；历史遍历可通过扫描 meta commit log 完成。
