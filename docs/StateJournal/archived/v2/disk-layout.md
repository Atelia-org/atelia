# StateJournal 磁盘布局与线格式（大杂烩）

> **状态**：临时大杂烩文件，从 `mvp-design-v2.md` 第四轮（R4）拆分提取。后续会进一步整理为更内聚的独立文档。
>
> **来源**：`atelia/docs/StateJournal/mvp-design-v2.md` §3.2（磁盘布局）、枚举值速查表、§5（实现建议）、§8（DataTail）、术语表编码层子节。
>
> **交叉引用**：
> - 变长编码（varint）/ ValueType → [primitive-serialization.md](primitive-serialization.md)
> - Dict DiffPayload 二进制布局 → [dict-delta.md](dict-delta.md)
> - ObjectVersionRecord / VersionIndex / Commit Record → [object-version-chain.md](object-version-chain.md)

---

## 1. 磁盘布局总述

本 MVP 采用 append-only 的 record log（长度可跳过 + CRC32C + 4B 对齐）。

---

## 2. 术语表：编码层

| 术语 | 定义 | 实现映射 |
|------|------|---------|
| **FrameTag** | RBF Frame 的顶层类型标识（位于 HeadLen 之后），是 StateJournal Record 的**唯一判别器**。采用 4 字节整数，16/16 位段编码：低 16 位为 RecordType，高 16 位为 SubType（当 RecordType=ObjectVersion 时解释为 ObjectKind）。详见 [rbf-interface.md](../Rbf/rbf-interface.md) `[F-FRAMETAG-DEFINITION]` 和本文档 `[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]` | `uint`（4 字节）|
| **RecordType** | FrameTag 低 16 位，区分 Record 顶层类型（ObjectVersionRecord / MetaCommitRecord） | `ushort` |
| **ObjectKind** | 当 RecordType=ObjectVersion 时，FrameTag 高 16 位的语义，决定 diff 解码器 | `ushort` 枚举 |
| **ValueType** | Dict DiffPayload 中的值类型标识。详见 [primitive-serialization.md](primitive-serialization.md) | `byte` 低 4 bit |

### 术语表：标识与指针（磁盘布局相关）

| 术语 | 定义 | 实现映射 |
|------|------|---------|
| **Ptr64** / **SizedPtr** | 8 字节文件偏移量。详见 [rbf-interface.md](../Rbf/rbf-interface.md) §2.2 | `ulong` |
| **ObjectVersionPtr** | 指向对象版本记录的 SizedPtr | `Ptr64` 编码值 |

---

## 3. FrameTag 编码

### 3.1 FrameTag 位段编码（§3.2.1, §3.2.2, §3.2.5）

**`[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]`** FrameTag 是一个 `uint`（4 字节整数），StateJournal MUST 按以下位段解释：

| 位范围 | 字段名 | 类型 | 语义 |
|--------|--------|------|------|
| 31..16 | SubType | `u16` | 当 RecordType=ObjectVersion 时解释为 ObjectKind |
| 15..0 | RecordType | `u16` | Record 顶层类型（ObjectVersion / MetaCommit） |

> **端序**：Little-Endian (LE)，即低位字节在前（字节 0-1 = RecordType，字节 2-3 = SubType）。

### 3.2 Packing/Unpacking 逻辑

应用层将 FrameTag（`uint`）解释为两个独立枚举：

- **Unpacking（解包）**：从 `uint` 提取 `RecordType`（低 16 位）和 `SubType`（高 16 位）
  - `RecordType = (ushort)(frameTag & 0xFFFF)`
  - `SubType = (ushort)(frameTag >> 16)`
- **Interpretation（解释）**：`RecordType` 决定如何解释 `SubType`
  - 当 `RecordType == ObjectVersion` 时，`SubType` 解释为 `ObjectKind`
  - 当 `RecordType != ObjectVersion` 时，`SubType` MUST 为 0（参见 `[F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER]`）
- **Packing（打包）**：从两个枚举组合回 `uint`
  - `frameTag = ((uint)subType << 16) | (uint)recordType`

### 3.3 RecordType（低 16 位）

| 值 | 名称 | 说明 |
|------|------|------|
| `0x0000` | Reserved | 保留，MUST NOT 使用 |
| `0x0001` | ObjectVersionRecord | 对象版本记录（data payload）|
| `0x0002` | MetaCommitRecord | 提交元数据记录（meta payload）|
| `0x0003..0x7FFF` | — | 未来标准扩展 |
| `0x8000..0xFFFF` | — | 实验/私有扩展 |

### 3.4 SubType（高 16 位）

**`[F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER]`** 当 `RecordType != ObjectVersionRecord` 时，`SubType` MUST 为 `0x0000`；Reader 遇到非零 SubType MUST 视为格式错误。

**`[F-OBJVER-OBJECTKIND-FROM-TAG]`** 当 `RecordType == ObjectVersionRecord` 时，`SubType` MUST 解释为 `ObjectKind`，Payload 内 MUST NOT 再包含 ObjectKind 字节。

### 3.5 FrameTag 完整取值表（MVP）

| FrameTag 值 | RecordType | ObjectKind | 说明 | 字节序列（LE）|
|-------------|------------|------------|------|---------------|
| `0x00010001` | ObjectVersion | Dict | DurableDict 版本记录 | `01 00 01 00` |
| `0x00020001` | ObjectVersion | Array | DurableArray 版本记录（未来）| `01 00 02 00` |
| `0x00000002` | MetaCommit | — | 提交元数据记录 | `02 00 00 00` |

> **FrameTag 是唯一判别器**：StateJournal 通过 `FrameTag`（独立 4B 字段）区分 Record 类型和对象类型。详见 [rbf-format.md](../Rbf/rbf-format.md) `[F-FRAMETAG-WIRE-ENCODING]`。
>
> **`[S-STATEJOURNAL-TOMBSTONE-SKIP]`** 墓碑帧通过 `FrameStatus.Tombstone` 标识，与 FrameTag 无关。StateJournal Reader MUST 先检查 `FrameStatus`，遇到 `Tombstone` 状态 MUST 跳过该帧，再解释 `FrameTag`。
>
> **`[F-UNKNOWN-FRAMETAG-REJECT]`** Reader 遇到未知 RecordType MUST fail-fast（不得静默跳过）。

### 3.6 ObjectKind（FrameTag SubType，当 RecordType=ObjectVersion）

> ObjectKind 现在编码在 FrameTag 高 16 位，扩展为 `u16`。

| 值 | 名称 | 说明 |
|------|------|------|
| `0x0000` | Reserved | 保留，MUST NOT 使用 |
| `0x0001` | Dict | DurableDict（MVP 唯一实现） |
| `0x0002..0x007F` | Standard | 标准类型（未来扩展） |
| `0x0080..0x00FF` | Variant | 版本变体（如 `DictV2`），兼容原 byte 约定 |
| `0x0100..0x7FFF` | Extended | 扩展标准类型 |
| `0x8000..0xFFFF` | Experimental | 实验/私有类型 |

**`[F-UNKNOWN-OBJECTKIND-REJECT]`** 当 `RecordType == ObjectVersionRecord` 时，Reader 遇到未知 `ObjectKind` MUST fail-fast（不得静默跳过）。

---

## 4. Data 文件（data file）

data 文件只承载对象与映射等 durable records（append-only），不承载 HEAD 指针。

### 4.1 I/O 目标（MVP）

- **随机读（random read）**：读取某个 Ptr64 指向的 record（用于 LoadObject/version replay）。
- **追加写（append-only write）**：commit 时追加写入新 records。
- **可截断（truncate）**：恢复时按 `DataTail` 截断尾部垃圾。

因此：mmap 不是 MVP 依赖；实现上使用 `FileStream` + `ReadAt/WriteAt/Append` 即可。

### 4.2 实现约束（避免实现分叉）

- MVP 的唯一推荐实现底座是 `FileStream`（或等价的基于 fd 的 `pread/pwrite` 随机读写 + append）。
- `mmap` 仅作为后续性能优化的备选实现，不进入 MVP 规格与测试基线。
- MVP 不依赖稀疏文件/预分配（例如 `SetLength` 预扩）；文件"有效末尾"以 meta 的 `DataTail`（逻辑尾部）为准。

> **帧格式**：Frame 结构（HeadLen/TailLen/Pad/CRC32C/Magic）、逆向扫描算法、Resync 机制详见 [rbf-format.md](../Rbf/rbf-format.md)。

### 4.3 DataRecord 类型判别

> FrameTag 与 ObjectKind 的定义及取值详见**§3 FrameTag 编码**。
>
> **Data 文件特有说明**：StateJournal 读取 Frame 后，根据 `FrameTag` 分流处理。`FrameTag` 区分 Record 的顶层类型；`ObjectKind` 用于 ObjectVersionRecord 内部选择 diff 解码器。

### 4.4 Ptr64 与对齐约束（MVP 固定）

- `Ptr64` 对应 [rbf-interface.md](../Rbf/rbf-interface.md) 中的 SizedPtr。
- 所有可被 `Ptr64` 指向的 Record，`Ptr64` 值等于该 Record 的 `HeadLen` 字段起始位置（即紧随分隔符 Fence 之后）。

data 文件内的"可被 meta 指向的关键 record"至少包括：

- `ObjectVersionRecord`（包括业务对象版本，以及 VersionIndex 作为 dict 的版本）

---

## 5. Meta 文件与 MetaCommitRecord

meta file 是 append-only 的 commit log：每次 `Commit(...)` 都追加写入一条 `MetaCommitRecord`。

> **帧格式**：Meta 文件与 Data 文件使用相同的 RBF 帧格式，详见 [rbf-format.md](../Rbf/rbf-format.md)。

### 5.1 MetaCommitRecord Payload

> **FrameTag = 0x00000002** 表示 MetaCommitRecord（参见 §3.5 FrameTag 完整取值表）。
> Payload 从业务字段开始，不再包含顶层类型字节。

meta payload 最小字段（**不含 FrameTag，FrameTag 由 RBF 层管理**）：

- `EpochSeq`（varuint，单调递增）
- `RootObjectId`（varuint）
- `VersionIndexPtr`（Ptr64，定长 u64 LE）
- `DataTail`（Ptr64，定长 u64 LE：data 文件逻辑尾部；`DataTail = EOF`，**包含尾部分隔符 Magic**。**注**：此处 Ptr64 表示文件末尾偏移量，不指向 Record 起点）
- `NextObjectId`（varuint）

### 5.2 打开（Open）策略

- 从 meta 文件尾部回扫，找到最后一个 CRC32C 有效的 `MetaCommitRecord`。
- **[R-META-AHEAD-BACKTRACK]** 若某条 meta record 校验通过，但其 `DataTail` 大于当前 data 文件长度（byte）/无法解引用其 `VersionIndexPtr`，则视为"meta 领先 data（崩溃撕裂）"，继续回扫上一条。

### 5.3 提交点（commit point）

- 一次 commit 以"meta 追加的 commit record 持久化完成"为对外可见点。

**[R-COMMIT-FSYNC-ORDER]** 刷盘顺序（MUST）：

1) 先将 data 文件本次追加的所有 records 写入并 `fsync`/flush（确保 durable）。
2) **然后** 将 meta 文件的 commit record 追加写入并 `fsync`/flush。

**[R-COMMIT-POINT-META-FSYNC]** Commit Point 定义（MUST）：

- Commit Point MUST 定义为 MetaCommitRecord fsync 完成时刻。
- 在此之前的任何崩溃都不会导致"部分提交"。

### 5.4 MetaCommitRecord payload 解析（MVP 固定）

> **前置条件**：RBF Scanner 已根据 `FrameTag == 0x00000002` 确定此为 MetaCommitRecord。

- 依次读取 `EpochSeq/RootObjectId/VersionIndexPtr/DataTail/NextObjectId`。
- 无需额外类型判别（FrameTag 已经是唯一判别器）。

---

## 6. 备选方案（非 MVP 默认）：单文件双 superblock

单文件 ping-pong superblock 仍是可行备选，但本文不将其作为 MVP 默认方案。

若采用该备选方案，superblock 至少包含：

- `Seq`：单调递增
- `EpochSeq`：`uint64 LE`，指示当前 HEAD 的 epoch 序号
- `RootObjectId`
- `DataTail`：Ptr64
- `NextObjectId`
- `CRC32C`

---

## 7. DataTail 与截断（恢复语义）

## term `DataTail` 数据尾指针
`DataTail` 是一个字节偏移量（byte offset），表示 data 文件的逻辑尾部。

### spec [R-DATATAIL-INCLUDES-TRAILING-FENCE] DataTail定义
@`DataTail` MUST 指向"有效数据末尾"，并包含尾部 Fence（即 `DataTail == 有效 EOF`）。

### spec [R-DATATAIL-TRUNCATE] DataTail截断规则
恢复时（上层依据其 HEAD/commit record 的语义决定使用哪条 @`DataTail`）：
1. 若 data 文件实际长度 DataTail：MUST 截断至 DataTail。
2. 截断后文件 SHOULD 以 Fence 结尾（若 `DataTail` 来自通过校验的 commit record）。

---

## 8. 实现建议：复用 Atelia 的写入基础设施

v2 的 commit 路径大量涉及"先写 payload、后回填长度/CRC32C/指针"的写法，适合直接复用 [ChunkedReservableWriter.cs](../../../src/Data/ChunkedReservableWriter.cs)：

- 它提供 `ReserveSpan + Commit(token)` 的回填能力，能把"回填 header/尾长/CRC32C"变成顺手的顺序代码。
- 它的"未 Commit 的 reservation 阻塞 flush"语义，能减少写入半成品被下游持久化的风险（仍需配合 fsync 顺序）。

落地方式（MVP 最小化）：

- data 文件：用一个 append-only writer 负责追加 record bytes。
- meta 文件：用 reservable writer 写入 `MetaCommitRecord`，在尾部写完后回填头部长度并写 CRC32C。
- 读取：用随机读（seek + read）实现 `ReadRecord(ptr)`，配合尾部扫尾解析 meta。
