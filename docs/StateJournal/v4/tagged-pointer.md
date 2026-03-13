
## Tagged-Pointer
采用类似JS V8引擎（Chrome/Node.js）的Tagged-Pointer方案，将常见值内联存储，边缘情况回退到用软指针查询内部Slot。一个特殊之处在于对double类型的处理，我们为了简单性和速度选择了牺牲精度，采用损失1bit尾数的有损的double存储。

伪代码示意：
```csharp
public readonly record struct ValueBox {
    private ulong _bits;
}
```

选择在高位一侧（bit层面左对齐）编码Tag-Code，低位一侧（bit层面右对齐）存储Payload（Inline-Value或者Slot-Index）。

存储的是值，不含变量类型。以C#语言举例，即输入变量不论是double还是float，都被编码为Lossy-Double类型；输入的int(42)和long(42)，内部都被编码为`0x4000_0000_0000_002A`。
采用确定性编码，每一个值都有唯一的二进制编码。

## BoxLzc 全景布局

> **BoxLzc** = `BitOperations.LeadingZeroCount(UInt64)`。BoxLzc 越小，可用 payload 越大。

### 设计原则

- **Inline 和 Slot 天然占据 BoxLzc 光谱两极**：Inline 存值需要大 payload（低 BoxLzc），Slot 存索引只需序号（高 BoxLzc）。
- **中间地带保留给 inline 演进**：BoxLzc 3-30 是 60-33 bit payload 的黄金储备，不急于占用。
- 以上皆为运行时内部状态的编码方案，进程关闭后可代码更新和重新布局，无需担心兼容性。

### 分配汇总

|LeadingZeroCount|PayloadBitCount|状态|用途|解码|
|---:|---:|---|---|
|0|63|确定|Inline-Double|`bits << 1`|
|1|62|确定|Inline-Nonnegative-Integer|`bits & 0x3FFF_FFFF_FFFF_FFFF`|
|2|61|确定|Inline-Negative-Integer|`bits \| 0xC000_0000_0000_0000`|
|3~23|60~40|未分配|未分配| |
|24|39|试运行|Heap-Slot|6bit HeapValueKind, 1bit ExclusiveBit, 32bit SlotHandle|
|25~26|38~35|未分配|未分配| |
|27|36|试运行|Durable-Ref|4bit DurableObjectKind, 32bit LocalId|
|28~30|35~33|未分配|未分配| |
|31|32|未分配|未分配| |
|32~61|31~2|未分配|未分配| |
|62|1|确定|Boolean|`bits & 1 != 0`|
|63|0|确定|Null| |
|64|0|确定|Uninitialized| |

### TABLED 技术方案

#### Per-type BoxLzc（当前方案）（TABLED）
每种内部类型占一个 BoxLzc 码位，dispatch 是零开销的 `switch(BoxLzc)`。
高频类型（string）分配低 BoxLzc（大容量），低频类型分配高 BoxLzc（小容量），匹配自然分布。

#### UserSlot (BoxLzc 31)（TABLED）
16-bit BytesTag 给用户完整的 tag 空间（65536 种自定义类型），16-bit SlotIndex（每种类型 65K 实例）。
StateJournal 提供 `{tag, bytes}` pair 的读写，**不解释 tag**——用户自行约定语义。
所有非 .NET 原语的领域类型（SemVer, BCP-47, MIME, 地理坐标, etc.）走此路径。

#### 候选演进方案：16-bit Plane 划分（TABLED）
> 将 BoxLzc 40-47 区间的琐碎空间统一为 16-bit 粒度的可分配单元（Plane），共 255 个。
> 每个 Plane 可独立分配给某个类型并拼接 SlotIndex，提供跨 BoxLzc 的容量拼接能力。
> **搁置理由**：Per-type BoxLzc 的单类型容量（最小 256K）远超当前需求；Plane 引入额外的 dispatch 间接层（查表开销）；从 Per-type BoxLzc 迁移到 Plane 无兼容性代价，可在真实需求出现时无损升级。

---

## BoxLzc 详细定义

### `BoxLzc:0` Rounded-Double
放弃1位尾数（mantissa）精度，换取永远inline。
为了使得误差均值为0，且避免进位或退位，采用如下舍入方法：
|最低2位尾数|舍入后尾数|误差ULP|
|---:|---:|---:|
|0b00|0b00|0|
|0b01|0b10|+1|
|0b10|0b10|0|
|0b11|0b10|-1|

候选编码算法：
```csharp
// 编码：tag bit63=1 + 63 bit payload (sign + exp + mantissa[51..1] with RTO sticky)
static ulong EncodeLossyDouble(ulong doubleBits) => (doubleBits >> 1) | (doubleBits & 1) | 0x8000_0000_0000_0000;

// 解码：左移 1 位，tag bit 自然溢出丢弃
static ulong DecodeLossyDouble(ulong encoded) => encoded << 1;
```

解码算法示意：`value = bits << 1`

### `BoxLzc:1` Inline-Nonnegative-Integer
后续62bit即为整数值。
解码算法示意：`value = bits & 0x3FFF_FFFF_FFFF_FFFF`。

### `BoxLzc:2` Inline-Negative-Integer
后续61bit连同前导的符号位值1作为补码表示法的负整数值。
解码算法示意：`value = bits | 0xC000_0000_0000_0000`。

### `待定` UserSlot
32-bit payload = BytesTag(16-bit) + SlotIndex(16-bit)。
StateJournal 不解释 BytesTag，用户自行约定语义。

### `BoxLzc:24` Heap-Slot
|bit|usage|
|---|---|
|0~31|heap pool SlotHandle|
|32|ExclusiveBit,用于标记目标槽位是否是独占的|
|33~38|heap value kind|

### `BoxLzc:27` Object-Ref
|bit|usage|
|---|---|
|0~31|Referenced target object's LocalId|
|32~35|DurableObjectKind|

#### Tagged-Value 序列化编码
重写 CBOR Major Type 5 (map, `0xA0..0xBF`) 语义。
布局: `0xA0 | (4bit Kind << 1) | (1bit PayloadLen)`。
- PayloadLen=0 → 后续 2 字节 (uint16 LE)
- PayloadLen=1 → 后续 4 字节 (uint32 LE)

编码器选择 narrow/wide: `LocalId.Value > 0xFFFF` 时用 4 字节，否则用 2 字节。

### `BoxLzc:62` Boolean
最低位表示Boolean值。
`0x2`: False
`0x3`: True

### `BoxLzc:63` Null
`0x1`，仅最低位为1。
等价于C#中的`null`语义，表示空引用。
MUST 读取时与 EVERY 引用类型相容。
MUST NOT 与 ANY 值类型相容。

### `BoxLzc:64` Uninitialized
`0x0`。64个bit全0。未初始化哨兵，用于内部的遗漏初始化检测，这不是一个对外可见的合法值。
设计目标是仅内部可见此状态，用于批量划分槽位，对外部永远都是明确赋值的。
目前在Upsert实现中，用到了`CollectionsMarshal.GetValueRefOrAddDefault`，会短暂出现此状态。
未来实现Mixed DurableList时，如果批量划分槽位也应显式初始化为合法值，用性能开销换取健壮性。

---

## 候选的设计素材（未分配 BoxLzc，供未来评估）

### Inline-Char-String
`ReadOnlySpan<byte>`，短字节串inline存储。

### Inline-Byte-String
`ReadOnlySpan<char>`，短字符串inline存储。

### Inline-Tagged-Bytes
开放性扩展点。
{byte bytesTag, ReadOnlySpan<byte> bytes}, 将短字节串内联编码。
bytesTag用于存储类型，bytes用于存储值。

### Tagged-Bits
开放性扩展点。**TODO:具体提供多少bit还待定，需要进一步分析。**
{byte bitsTag, ulong/uint bits}, 将短bit串内联编码。
bitsTag用于存储类型，bits用于存储值。
可能的用途：带类型的enum、

### Inline-Timestamp
**TODO:不确定是否能让常见值内联。可能需要再搞个epoch时间点，为近未来优化，Agent层软件是首要用户。**

### Inline-TimeSpan
**TODO:让常见值内联希望较大，因为短间隔更常见。**

### 搁置的候选 InternalSlot 类型（v2 考虑）
- `Int128` / `UInt128`：.NET 7+ 大整数
- `DateOnly`：.NET 6+ 日历日期，可能作为 Inline 整数编码
- `TimeOnly`：.NET 6+ 一天内时刻
- `BigInteger`：任意精度整数（变长）
- 其他建议走 UserSlot：`Uri`(=string), `Version`(=packed long), `IPAddress`(=byte[]), `Complex`(=2×double)
