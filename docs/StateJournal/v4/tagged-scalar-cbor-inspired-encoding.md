# Tagged Scalar Encoding: CBOR-Inspired, Little-Endian Variant

## 结论

`StateJournal.Serialization` 中的 `TaggedNonnegativeInteger`、`TaggedNegativeInteger`、`TaggedFloatingPoint`、`TaggedBoolean`、`TaggedNull` 采用了“借用 CBOR major type 0 / 1 / 7 的头部空间，但保留 little-endian payload”的方案。

这不是标准 CBOR，也不追求与 RFC 8949 的字节流兼容。

目前为止，`StateJournal` 与 CBOR 的关联仅限于：

1. 复用 major type 0 表示非负整数。
2. 复用 major type 1 表示负整数，并采用同样的负数映射公式：`n = -1 - value`。
3. 复用 major type 7 表示 simple values 与 IEEE 754 half/single/double。
4. 复用 `false=true=null` 与 `half/single/double` 的头字节编号。

除此之外，不应把当前编码视为“兼容 CBOR”。

## 当前规则

### 非负整数

- `0..23`：单字节 inline，头字节范围 `0x00..0x17`
- `24+`：沿用 CBOR 的 `0x18/0x19/0x1A/0x1B` 作为 1/2/4/8 字节扩展头
- 扩展 payload 使用 little-endian

### 负整数

- 采用 CBOR 同款映射：`payload = -1 - value`
- 因此：
  - `-1 -> payload 0`
  - `-24 -> payload 23`
  - `-25 -> payload 24`
- `0..23` 的 payload 直接 inline 到头字节 `0x20..0x37`
- 扩展头使用 `0x38/0x39/0x3A/0x3B`
- 扩展 payload 仍使用 little-endian

### 浮点与 simple values

- `false = 0xF4`
- `true = 0xF5`
- `null = 0xF6`
- `half = 0xF9`
- `single = 0xFA`
- `double = 0xFB`

浮点的 16/32/64 位 payload 也采用 little-endian。

`TaggedFloatingPoint` 继续遵循“在保持 bit-exact 的前提下尽量选择更短表示”的策略：

1. 非 NaN 时优先尝试 Half。
2. 不能精确表示为 Half 时尝试 float。
3. 仍不能 bit-exact 表示时写 double。
4. NaN 一律保留原始 64 位 payload。

## 为什么这样做

选择这条路线的理由是：

1. CBOR 在 major type 0 / 1 / 7 上与 `StateJournal` 的“自描述标量”模型天然接近。
2. 负整数采用 `-1 - value` 后，`0..23` 与 `-1..-24` 形成漂亮对称，且 `long.MinValue` 的处理更自然。
3. 仓库其余二进制编码普遍采用 little-endian，延续这一点能减少局部特殊规则。
4. 当前阶段并不打算让 `StateJournal` 直接映射 CBOR 的 text/array/map 语义，因此没有必要追求完整 CBOR 对齐。

## 明确不做的事

当前设计明确没有承诺以下事项：

1. 不承诺与 RFC 8949 的 wire format 兼容。
2. 不承诺复用 CBOR major type 3 / 4 / 5 作为 `StateJournal` 的 string / array / map 编码方案。
3. 不承诺可被通用 CBOR 解码器直接解析。
4. 不承诺未来的 `TaggedString`、容器 diff 格式、`DurableObjectRef` 会沿用 CBOR 结构。

## 实施提醒

后续文档和代码中，应该将这套编码描述为：

- “CBOR-inspired scalar encoding”
- 或 “little-endian variant of CBOR major type 0/1/7 for StateJournal tagged scalars”

避免把它表述成“CBOR baseline”或“兼容 CBOR”，以免后续 agent 或人工维护者误判协议边界。
