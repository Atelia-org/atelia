# 原始类型序列化编码

> **来源**：从 `mvp-design-v2.md` 提取（§3.2.0, §3.2.0.1, §3.4.2 ValueType 表）。
> 本文档是这些内容的 SSOT（唯一权威来源）。

---

## ValueType（低 4 bit）

| 值 | 名称 | Payload |
|------|------|---------|
| `0x0` | Val_Null | 无 |
| `0x1` | Val_Tombstone | 无（表示删除） |
| `0x2` | Val_ObjRef | `ObjectId`（varuint） |
| `0x3` | Val_VarInt | `varint`（ZigZag） |
| `0x4` | Val_Ptr64 | `u64 LE` |

---

## 变长编码（varint）决策

本 MVP 允许在对象/映射的 payload 层使用 varint（ULEB128 风格或等价编码），主要目的：降低序列化尺寸，且与"对象字段一次性 materialize"模式相匹配。

MVP 固定：

- 除 `Ptr64/Len/CRC32C` 等"硬定长字段"外，其余整数均可采用 varint。
- `ObjectId`：varint。
- `Count/PairCount` 等计数：varint。
- **[S-DURABLEDICT-KEY-ULONG-ONLY]** `DurableDict` 的 key：`ulong`，采用 `varuint`。

---

## varint 的精确定义

为避免实现分歧，本 MVP 固化 varint 语义为"protobuf 风格 base-128 varint"（ULEB128 等价），并要求 **[F-VARINT-CANONICAL-ENCODING]** **canonical 最短编码**：

- `varuint`：无符号 base-128，每个字节低 7 bit 为数据，高 1 bit 为 continuation（1 表示后续还有字节）。`uint64` 最多 10 字节。
- `varint`：有符号整数采用 ZigZag 映射后按 `varuint` 编码。
	- ZigZag64：`zz = (n << 1) ^ (n >> 63)`；ZigZag32：`zz = (n << 1) ^ (n >> 31)`。
- **[F-DECODE-ERROR-FAILFAST]** 解码错误策略（MVP 固定）：遇到 EOF、溢出（超过允许的最大字节数或移位溢出）、或非 canonical（例如存在多余的 0 continuation 字节）一律视为格式错误并失败。

**(Informative / Illustration)** 以下 ASCII 图示仅供教学参考，SSOT 为上述文字描述和公式。

```text
VarInt Encoding (Base-128, MSB continuation)
=============================================

值 300 = 0x12C = 0b1_0010_1100
编码：  [1010_1100] [0000_0010]
         └─ 0xAC     └─ 0x02
         (cont=1)    (cont=0, end)

解码：(0x2C) | (0x02 << 7) = 44 + 256 = 300

边界示例：
  值 127 → [0111_1111]          (1 byte, 最大单字节)
  值 128 → [1000_0000 0000_0001] (2 bytes)
  值 0   → [0000_0000]          (1 byte, canonical)
```
