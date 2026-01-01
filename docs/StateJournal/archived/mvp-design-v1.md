# DurableHeap MVP 设计初稿（Working Draft）

> 日期：2025-12-16
>
> 目的：把已达成共识的部分固化成“可开工”的 MVP 规格；把不确定项明确列为 Open Questions，避免在实现期反复拉扯。
>
> 核心取舍：MVP 优先保证 **可恢复性（crash-safe）** 与 **可随机跳转（random access）**；在此基础上追求尽可能多的 **zero-copy（对 bytes 的零拷贝视图）**。

---

## 1. 术语

- **Durable Pointer / Ptr32**：文件内指针（4B，无符号），以 4B 为单位编码，**绝对文件偏移**：`ByteOffset = Ptr32 * 4`；Data Area 起始为 8KB（双 Superblock 之后），因此有效的 RecordPtr32 必须满足 `Ptr32 >= 2048` 且 4B 对齐。
- **Ptr32 的 null 约定**：
  - `Ptr32 = 0` 是“指针级别的 null”（不可解引用），仅用于 Superblock.RootPtr 的初始态或“暂无 root”。
  - “值存在但为空”使用 `Tag=0x00`（Null value）。
  - “集合中不存在该 key”属于 key absent（`TryGet` 返回 false），不等价于 Null value。
- **RecordPtr32**：指向某个 Record 起始字节（Tag）的 Ptr32（满足“可从 Header 的 `TotalLen` 跳过整个 record”）。
- **ValueOffset32**：相对某个 Record 起始位置（Tag）的 `int32` 偏移，用于定位 record 内某个 value 的 Tag（inline value 没有独立 record header/footer）。
- **Record**：一个可独立校验边界与完整性的对象/值编码单元。
- **Inline（值类型）**：数据直接嵌入到父结构中，不需要额外 Ptr。
- **Flat（引用类型）**：对象在文件中有独立 Record，父结构通过 Ptr 引用。
- **Zero-copy（本 MVP 的语义）**：
  - 对 `string/blob` 等 **字节区段**：wrapper 能返回 `ReadOnlySpan<byte>` / `ReadOnlySpan<char>` 的视图而不复制 payload bytes。
  - 对 `int/bool` 等 **标量数值**：允许“解码到寄存器/栈变量”；不把“必须算出数值”视为破坏 zero-copy。

MVP 决策（消除不确定性）：
- 字符串存储仅支持 UTF-16（Little-endian，2 字节/char），不支持 UTF-8。
- `varint` 暂不引入；所有元数据与数值统一用定长 `int32/uint32`。
- 只实现“`int32` 作为 key 的 dict”（足够覆盖大量场景），不实现字符串 key 与字符串池。

UTF-16 实现注意（.NET 视角）：
- `StringUtf16` 的 payload bytes 需要满足 2B 对齐，才能安全/高效地投影为 `ReadOnlySpan<char>`；因此本 MVP 的编码中为其预留了对齐 padding，并要求实现方在解析时做边界/对齐校验。
- `string`（托管对象）本质上必须 materialize（分配）；zero-copy 语义主要面向 `ReadOnlySpan<char>` 视图路径。

Null 语义层次（概念澄清）：

| 层次 | 表示 | 语义 | 使用场景 |
|------|------|------|----------|
| 指针 | `Ptr32 = 0` | 无有效引用（不可解引用） | Superblock.RootPtr 初始态 |
| 值 | `Tag = 0x00` | 显式空值（值存在） | Dict value 为 null |
| 集合 | key absent | 该 key 未被写入 | `TryGet` 返回 false |

---

## 2. 文件布局（单文件 + 头部双 Superblock）

```
[Superblock A: 4KB][Superblock B: 4KB][Data Area ...]
```

### 2.1 Superblock（Ping-Pong）

每个 Superblock 固定 4KB，包含：
- Magic + Version
- Seq（单调递增）
- RootPtr（4B，Ptr32）
- DataTail（4B，必填：逻辑追加尾，Ptr32；用于快速定位/截断）
- CRC32C（Castagnoli；建议实现：.NET `System.IO.Hashing.Crc32C`；覆盖除 CRC 字段外的内容）

写入时轮流覆盖 A/B，并保证写入顺序：先写数据区 Record，再写 Superblock（提交点）。

提交与刷盘顺序（mmap/OS 缓存下必须显式约束）：
- Commit 的“对外可见点”是 Superblock 写入成功且刷盘完成。
- Commit 时必须先把 record 写成“最终态”（含 `TotalLen` 回填、footer `TotalLen` 与 CRC 写入），再确保 Data Area 中本次追加的 record 字节已刷盘，最后再刷盘 Superblock；否则可能出现“Superblock 已指向新 RootPtr，但对应 record bytes 仍未落盘/仍是半成品”的撕裂。
- 若 Data Area 与 Superblock 处在同一个 mmap view 中：仍需在写入 Record 完整字节后先调用一次 flush（确保数据页先落盘），再写入/更新 Superblock 并再次 flush（确保提交点落盘）。
- 若采用不同 view：先 flush Data Area view，再 flush Superblock view。

耐久性说明（MVP 口径）：
- 刷盘 API 的“落盘强度”可能因平台与文件系统策略而异；MVP 以崩溃注入测试可复现地验证恢复逻辑为准绳。

MVP 约束（为了降低实现复杂度）：
- 文件预先扩展到一个最大逻辑大小（可用稀疏文件），MVP 目标上限为 4GB。
- Data Area 在该范围内 append；MVP 不做“动态 remap/滑动窗口映射”。

恢复时：读取 A/B，校验 CRC，取 Seq 最大者。

---

## 3. Data Area：Append-only Record Log

### 3.1 顶层 Record 边界（强约束：可从 Ptr 直接跳过）

为了支持：
- 从任意 Ptr 位置 **O(1) 跳过整个对象**（不扫描成员）
- 从文件尾部向前 **恢复/回扫**

MVP 采用 **头尾都存长度**：

```
[Tag:1B]
[HeaderLen:2B]                // 便于版本演进；MVP 可固定为常量
[TotalLen:4B]                 // 总长度（含 header+payload+footer）
[Payload ...]
[TotalLen:4B]
[CRC32C:4B]                    // 覆盖除 CRC 字段外的所有字节（包含 footer 的 TotalLen）

编码约定（MVP 固定）：
- 所有多字节整数均为 Little-endian。
- `HeaderLen` 表示“从 Tag 起（含 Tag）到 payload 起始位置”的字节数；MVP 固定为 7（1+2+4）。
- `FooterLen` 固定为 8（4B TotalLen + 4B CRC32C）。
```

约束：
- Ptr 指向 Tag。
- `TotalLen` 必须在 Header 中可读（否则无法从 Ptr 局部跳过/随机访问）。
- Footer 的 `TotalLen` + CRC 用于尾部回扫与损坏检测。

指针约束（MVP）：
- 只有 `RecordPtr32` 才承诺“可从 Header 的 `TotalLen` 跳过整个 record”。
- record 内部成员定位使用 `ValueOffset32`（相对 record 起点），而不是把 inline value 当作可跳过的 record。

MVP 上限：
- `TotalLen`（4B）必须满足 `TotalLen <= 2GB`（建议用 `int32` 表达并拒绝负值/溢出）。
- 单文件目标上限为 4GB（Ptr32 足够）。

MVP 固定约定（减少实现分歧）：
- Endianness：所有固定宽度整数一律 **Little-endian**。
- `HeaderLen`：MVP 固定为 7（`Tag(1) + HeaderLen(2) + TotalLen(4)`）。
- `FooterLen`：MVP 固定为 8（`TotalLen(4) + CRC32C(4)`）。
- CRC32C：必须基于 **最终 record bytes** 计算（包含 header 回填后的 `TotalLen`），并写入 footer。
- 任何由 `TotalLen / Count / FieldCount` 推导出的 span 长度与 table byte size，必须在计算时使用 `checked(...)` 并最终满足 `<= int.MaxValue`。

对齐（MVP）：
- Record 起始位置（Tag 所在 byte offset）必须为 4B 对齐。
- Flat record 内部可包含 padding，以保证 offset table 与 value data 满足对齐假设（例如 4B 对齐）。

指针单位与对齐不变量（MVP 固定，便于实现与校验）：
- 所有 `ValueOffset32` 必须满足 `ValueOffset32 % 4 == 0`。
- 每个 record 的 `TotalLen` 必须满足 `TotalLen % 4 == 0`（writer 需要用 0 填充 padding 使 footer 落在 4B 边界）。

> 说明：只把 `TotalLen` 放在 footer（“先写数据后写长度”且不回填头部）会导致：从 Ptr 出发无法定位 Record 末尾，从而无法局部跳过成员、更无法在对象内做惰性索引构建。

### 3.2 写入策略（支持 IBufferWriter）

- 写入时：
  1) 写 Tag + Header（其中 `TotalLen` 先占位 0）
  2) 写 Payload（同时在内存中收集必要的 offsets）
  3) 回填 Header 中的 `TotalLen`（形成最终 header bytes）
  4) 写 Footer 的 `TotalLen`
  5) 基于最终 record bytes 计算 CRC32C 并写入 footer

重要：
- CRC32C 必须在 header 的 `TotalLen` 回填完成后计算；否则校验时会出现“CRC 覆盖的字节序列与实际落盘不一致”。

- 需要一个支持“预留 + 回填”的 writer（参考 atelia 的 `ChunkedReservableWriter` 思路）。

---

## 4. 二进制编码：混合风格（Inline + Flat）

### 4.1 Tag 表（MVP）

- `0x00` Null
- `0x01` Bool
- `0x02` Int32 (Inline)
- `0x03` StringUtf16 (Inline)
- `0x04` Ref32 (Inline，Ptr32；用于引用 flat record)
- `0x10` DictInt32 (Flat)
- `0x11` Array (Flat)
- `0x20` Blob (Flat，可选：给 ToolResult 大块数据/多模态留口)

### 4.2 Inline 值类型（MVP 仅定长 int32 / UTF-16）

#### Int32

```
[Tag=0x02][Value:int32]
```

说明：
- 先统一用定长 `int32`，降低实现与测试复杂度。

#### StringUtf16

```
[Tag=0x03][Pad:3B][CharLen:int32][UTF16-LE bytes (CharLen*2)...]
```

取舍：
- 存储与核心使用环境（中文）一致，避免 UTF-8/UTF-16 反复转码。
- wrapper 可 zero-copy 地返回 `ReadOnlySpan<char>`（实现上从 UTF16 bytes 形成 char 视图）。
- `Pad:3B` 用于把 `CharLen:int32` 对齐到 4B 边界，同时保证 UTF-16 bytes 的起始位置为 4B 对齐（自然也满足 2B 对齐）。
- writer 必须在 UTF-16 bytes 之后补齐 0 填充，使“下一个 value 的 Tag”仍落在 4B 对齐边界上。
- 若后续仍有字段或 footer，需要在 UTF-16 bytes 末尾按需补 0，使下一个 value 的 Tag 以及 record 的 `TotalLen` 继续满足 4B 对齐（`ValueOffset32 % 4` 与 `TotalLen % 4`）。

#### Ref32

```
[Tag=0x04][Ptr32:uint32]
```

语义：
- 指向另一个 flat record 的起始位置（Tag）。
- Ptr32 以 4B 为单位编码：`ByteOffset = Ptr32 * 4`。

### 4.3 Flat 引用类型（固定布局，支持惰性随机访问）

#### DictInt32 Record（Flat）

目标：
- 能只读取 Header/OffsetTable，就可 O(1) 定位任意字段 value 的起始位置。
- MVP 仅支持 `int32` key，避免字符串 key 与字符串池复杂度。

建议布局（MVP 版本 1）：

```
[RecordHeader...]
[ObjHeader]
  [FieldCount:4B]
  [EntryTableOffset:4B]        // 相对 Record 起始的偏移

[EntryTable]                    // 4B 对齐
  repeat FieldCount times:
    [Key:int32]
    [ValueOffset:4B]            // 指向 value 的 Tag（value 可以是 Inline 或 Ref32）

[ValueData ...]                 // 紧随其后，顺序写入（必要时插入 padding 以保持 4B 对齐）
```

说明：
- 固定宽度 offsets 使得 wrapper 不需要扫描变长表。
- `ValueOffset` 指向 value 的 Tag，从而可递归解析。
- MVP 约束：`EntryTable` 必须按 `Key` 升序排序写入；读取端必须使用二分查找。

写入算法（MVP，推荐实现方式）：

目标：在保持“EntryTable 在前、ValueData 在后”的布局下，做到：
- keys 有序（便于二分查找）
- `ValueOffset32` 正确（指向每个 value 的 Tag）
- 满足 4B 对齐与 `TotalLen % 4 == 0`

推荐步骤：
1) 收集待写入条目：在内存中准备 `List<(int Key, ValueSpec Value)>`。
2) 依据 `Key` 升序排序；若存在重复 key：MVP 建议直接抛错（或定义“后写覆盖前写”的确定性规则）。
3) 写入 record header（固定 7B）后，先补齐 0 padding 使写入位置回到 4B 对齐。
4) 写入 ObjHeader，并设置 `EntryTableOffset` 指向接下来的 EntryTable 起点（该起点必须为 4B 对齐）。
5) 预留 EntryTable 区域（用于回填）：大小为 `FieldCount * 8` 字节（每项 8B：`Key:int32 + ValueOffset32:int32`）。
  - 将这段预留区初始化为 0（便于崩溃注入时诊断；也避免脏字节影响确定性）。
6) 依次写入 ValueData：
  - 在写每个 value 前，记录 `valueTagOffset = (currentWritePos - recordStartPos)`。
  - 写入 value（Tag + payload；若是 Ref32 则写 Tag=0x04+Ptr32）。
  - 在 value 结束后补齐 0 padding，使 `currentWritePos` 回到 4B 对齐。
  - 将该条目的 `ValueOffset32 = valueTagOffset` 缓存到内存数组。
7) 回填 EntryTable：写回 `Key` 与对应的 `ValueOffset32`（按排序后的顺序）。
8) Finalize record：
  - 如有需要，先补齐 0 padding，确保 footer 写入位置满足 4B 对齐。
  - 计算 `TotalLen`，回填 header 的 `TotalLen`，并写入 footer 的 `TotalLen`。
  - 基于最终 record bytes 计算 CRC32C（覆盖除 CRC 字段外的所有字节，包含 footer 的 `TotalLen`），最后写入 CRC32C 字段。

注：
- Step 4 的“预留 + 回填”与整体 record 的 `TotalLen` 回填/CRC 计算顺序不冲突：EntryTable 回填发生在 footer CRC 计算之前即可。
- 这套写法只需要一个“可预留/回填”的 writer；不要求把 value 临时编码到中间缓冲区。

#### Array Record（Flat）

```
[RecordHeader...]
[ArrHeader]
  [Count:4B]
  [ElemTableOffset:4B]

[ElemOffsetTable]
  [Offset0:4B][Offset1:4B]...[OffsetN-1:4B]

[ElemData...]
```

- 元素是 Inline value 或 Ptr 引用（由 Tag 决定）。
- wrapper 可在首次访问时 mmap 读取 offset table，并在后续按需读取元素。

---

## 5. Wrapper 语义：zero-copy 与惰性读取

- `DurableRef<T>`：持有 `RecordPtr32` + `DurableHeap`（或 mmap view）引用；不预解析整对象。
- `DurableString`：持有 `(RecordPtr32 owner, ValueOffset32 valueOffset)` 与 `CharLen`；提供 `ReadOnlySpan<char>` 视图（UTF-16）与 `string Materialize()`（可选）。
- `DurableDictInt32` / `DurableArray`：
  - 初次仅读 header（含 `TotalLen`、count、table offset）。
  - 首次索引访问时读取 offset table（一次性，可能会缓存到托管数组；这是“索引缓存”，不是 payload copy）。

> 关键澄清：
> - “zero-copy”主要针对 payload bytes；offset table 缓存到托管内存属于性能优化，不等同于把整个对象反序列化成普通 C# 对象。

### 5.1 .NET 实现策略（unsafe 边界与 wrapper 形态）

本 MVP 采用一个“很稳的三层分法”，目标是：
- 让 `unsafe` 只出现在少数边界点（便于核查与测试）。
- wrapper 层保持“视图/访问器”语义（lazy + zero-copy bytes），避免退化成一次性反序列化。

#### Layer 0：映射与生命周期（允许极少量 unsafe）

职责：
- 打开/创建文件（稀疏文件也在这里处理）。
- 创建 `MemoryMappedFile` + 单一长期存活的 View（MVP 可直接映射 Data Area 或整个文件）。
- 获取 view 基址指针（一次性），并保证其生命周期覆盖所有 wrapper 的使用。

建议形态：
- `DurableHeapFile : IDisposable` 持有：
  - `MemoryMappedFile`
  - `MemoryMappedViewAccessor`
  - `SafeMemoryMappedViewHandle`
  - `IntPtr basePtr`（或 `nint`，语义为“view 的第一个字节地址”）与 `int/long mappedLength`
  - `long viewFileOffset`（该 view 对应的文件起始偏移；若映射整个文件则为 0）
- 在构造时 `AcquirePointer`，在 Dispose 时 `ReleasePointer`。

重要注意（指针与 view 的细节）：
- `AcquirePointer` 返回的指针需要加上 `SafeMemoryMappedViewHandle.PointerOffset`，才能得到 view 的第一个字节地址。
- 本文中 `basePtr` 约定为“已加过一次 `PointerOffset` 后的 view 起始地址”。
- 若 view 对应文件偏移为 `viewFileOffset`，则文件绝对偏移 `fileOffset` 的地址为：`basePtr + (fileOffset - viewFileOffset)`。

实现细节提醒（避免误用）：
- `PointerOffset` 是 `long`；指针运算时建议经由 `nint`/`nuint` 做显式转换并做范围检查（避免在 32-bit 进程或极端映射长度下发生截断）。
- `PointerOffset` 只能加一次：`basePtr` 一旦定义为“view 的第一个字节”，后续所有地址计算都应基于该语义，避免重复加 offset。

说明：
- “临时构造 `ReadOnlySpan<byte>`”的成本通常非常小（本质是 `(ptr,len)` 视图）；真正需要避免的是“频繁创建/销毁 view”与“跨层频繁 FFI/系统调用”。
- wrapper 不持有 `ReadOnlySpan<byte>` 字段（它是 `ref struct`，不能上堆）；wrapper 持有 `DurableHeapFile`（强引用）+ `RecordPtr32`/`ValueOffset32`。

MVP 选择（明确化，避免实现期摇摆）：
- 采用“单一长期 view + 单一 basePtr”策略；文件预扩展到固定最大大小（稀疏可用）。
- 不支持文件超过映射范围后的自动增长；超过即报错或需要新文件（后续版本再做 remap/分段）。

#### Layer 1：二进制读取与解析（尽量纯安全代码）

职责：
- 在 `ReadOnlySpan<byte>` 上实现：
  - Little-endian 读取（建议统一用 `BinaryPrimitives.*LittleEndian`）。
  - 固定宽度整数读取（MVP 不引入 varint）。
  - Record header/footer 校验（TotalLen、CRC）。
  - DictInt32/Array 的 offset table 读取与边界检查。

（MVP 注）本文档中的“JObject/JArray”统一对应 `DictInt32/Array`。

建议工具箱：
- `System.Buffers.Binary.BinaryPrimitives`
- `System.Runtime.InteropServices.MemoryMarshal`（必要时）
- 尽量避免 `Marshal.PtrToStructure` 这类 API（容易引入布局/对齐/版本化的隐性坑）。

关键约束：
- offset table / count / table offset 等“随机定位必经元数据”使用固定宽度；解析时必须可在 O(1) 定位成员。
- 对任何来自文件的 offset/len，都要做“范围检查”（防止损坏文件导致越界读）。

建议把“范围检查”写成硬性检查清单（MVP 必做）：
- 先读取最小 header 前缀（至少含 Tag + HeaderLen + TotalLen），再根据 `TotalLen` 构造 record span。
- 校验 `HeaderLen`、`TotalLen` 的基本关系：`HeaderLen` 合理、`TotalLen >= HeaderLen + FooterLen`。
- 校验 table offset 在 record 内，且满足对齐（若假设 4B 对齐则必须 4B 对齐）。
- 校验 table byte size 计算不溢出（`checked(FieldCount * 8)` / `checked(Count * 4)`），且完全落在 record 边界内。
- 校验每个 `ValueOffset` 指向 record 内的有效 Tag 位置。

#### Layer 2：Wrapper（视图对象；lazy + zero-copy bytes）

职责：
- 对外提供符合直觉的 API（如 `TryGet`、`GetAt(i)`），内部按需读取 header/table/payload。
- 对 payload bytes（UTF-16 string bytes、blob）提供 zero-copy 视图路径；对 `string` 提供显式 materialize 路径。

推荐形态（MVP 取舍）：
- Wrapper 可以是 class/struct（持 `DurableHeapFile` + `Ptr`），成员访问时调用：
  - `var header = heap.ReadRecordHeader(recordPtr)`（只读固定长度 header 前缀）
  - `ReadOnlySpan<byte> record = heap.GetSpan(recordPtr, header.TotalLen)`
  - 然后 `record.Slice(...)` 做局部访问

注意：
- 若要做极致性能，可把“解析结果”设计成 `ref struct XxxView`（只在方法栈上活），而对外持久对象只保存 `(heap, ptr)`。
- offset table 缓存到托管数组属于“索引缓存”，允许；但 payload bytes 不应复制。

生命周期规则（必须写清楚，否则很容易用出 UAF）：
- 任何从 wrapper 返回的 `ReadOnlySpan<byte>` 都只在其 owner（`DurableHeapFile`）未 Dispose 的前提下有效；调用方不得在 heap 释放后继续使用旧 span。
- MVP 可在所有对外入口加 `heap.ThrowIfDisposed()` 保护，并在文档中把“Dispose 后 wrapper 失效”视为硬契约。

#### 次要目标：可替换的解析核心接口（不与 MVP 冲突时再做）

为未来替换实现（例如不同 mmap 策略、甚至 native parser）预留最小接口，但不强制抽象过度：
- `IByteViewProvider`：`ReadOnlySpan<byte> GetSpan(long offset, int length)`（或 `TryGetSpan`）
- `IRecordReader`：`RecordHeader ReadHeader(long ptr)`、`bool TryValidateRecord(long ptr, out RecordInfo info)`

约束：
- 接口应“粗粒度”，避免把每个字段访问都做成一次接口调用（否则会引入间接成本并阻碍 JIT 内联）。
- MVP 阶段优先直连实现；接口仅作为注入点保留，不作为所有代码的必经路径。

---

## 6. 崩溃恢复

恢复算法：
1) 读取 Superblock A/B，取 Seq 最大且 CRC 有效者，得到 RootPtr。
2) 可选：校验 RootPtr 指向的 Record（header+footer+CRC）。
3) 若 RootPtr 指向损坏 Record：回退到另一个 Superblock（Seq 次大者）。

对 DataArea 尾部垃圾：
- 必须 truncate 到 DataTail（DataTail 为必填字段）。
- 仍可保留“尾部回扫”作为诊断/修复工具（不作为 MVP 必需路径）。

---

## 7. MVP 决策清单（已确定）

- 单文件；Append-only；不做对象销毁/回收。
- Ptr32 = 4B（4B 单位），Record 起始 4B 对齐。
- 头部双 Superblock Ping-Pong 提交。
- Record 必须支持从 Ptr 直接跳过（Header 中必须有 TotalLen）。
- Superblock.DataTail 为必填字段；恢复时必须以 DataTail 截断尾部垃圾。
- 字符串仅支持 UTF-16（Little-endian），不支持 UTF-8。
- 仅支持 `int32` key 的 dict（`DictInt32`），以及数组（`Array`）。
- 暂不引入 varint；所有元数据与数值统一用定长 `int32/uint32`。
- 引用类型（Dict/Array）使用固定宽度 offset table，确保随机访问。
- DictInt32：EntryTable 必须排序写入，读取端二分查找。
- 校验：Record footer 使用 CRC32C（Castagnoli）；Superblock 也使用 CRC32C。

### 7.1 稳定性分级（指导快速演进期的“不变量”选择）

以下约束预计不会轻易改变（改动成本高，需要迁移/版本化策略）：
- Record header 中必须包含 `TotalLen`（可跳过性的基石）。
- Ptr32 以 4B 为单位的编码方式与“绝对文件偏移”的语义。
- Little-endian。
- CRC32C 覆盖范围契约（superblock/record 的校验字段存在且可验证）。

以下约束是 MVP 选择，可能随快速演进调整（但需要版本化/兼容策略）：
- `HeaderLen` / `FooterLen` 的具体常量值。
- 字符串仅 UTF-16。
- Dict 仅 int32 key。
- 单文件 4GB 上限与 record 2GB 上限。

---

## 8. Open Questions（需要专项研究/实验）

1) `string` 的对外 API：
  - 是否提供 `ReadOnlySpan<char> Chars` + `string Materialize()` 双路径？materialize 是否缓存？

2) Dict 查找策略：
  - 已决策：MVP 固定“排序写入 + 二分查找”。开放问题仅保留为：是否需要在 header 中记录“已排序”标志以支持未来的多策略兼容。

3) 校验策略：
  - CRC32C 的具体实现口径是否固定为 .NET 的 `System.IO.Hashing.Crc32C`（建议是）。
  - 是否需要 per-record magic（除 Tag 外再加 magic）？
  - Header 版本化与兼容策略（尤其是 `HeaderLen` 变化时）。

---

## 9. 下一步（建议）

- 写一个最小 `DurableHeapFile`：mmap + superblock 读写 + append record + root flip。
- 写 `DurableDictInt32` 的 lazy 读取原型：
  - `TryGet(int key, out DurableValue value)`
  - 先读 header/table，再用 offset 定位 value。
- 用 micro-benchmark 比较：
  - Dict 查找：线性扫描 vs（可选）排序后二分
  - offset table 在托管数组缓存 vs 每次从 mmap 读取
