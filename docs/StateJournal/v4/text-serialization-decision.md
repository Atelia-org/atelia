# Text Serialization Decision for StateJournal Binary Format

## 结论

当前阶段，`StateJournal` 不引入与二进制格式同构的正式文本序列化格式。

当前推荐路线是：

1. 继续以二进制序列化作为唯一正式 wire format。
2. 允许二进制实现继续采用针对 `Span<byte>` / `ReadOnlySpan<byte>` 的类型特化优化。
3. 如果需要面向人类的可读表示，优先补“诊断表示（diagnostic notation / inspector output）”，而不是补一套正式的文本 serializer + parser。

这是一项阶段性决策，不是永久禁止文本表示。

## 背景

`StateJournal` 当前的二进制序列化正在快速成形：

1. tagged scalar 已经确定为“CBOR-inspired major type 0/1/7 + little-endian payload”方案。
2. 读侧最低两层骨架已经落地，包括 `BinaryDiffReader`、`TaggedValueDispatcher`、`DictDiffParser`。

在这个阶段，如果为了“可能会需要文本同构序列化”而提前抽象出一套同时服务 binary/text 的统一 Reader/Writer 框架，很容易把底层实现拉回最低公共抽象，削弱二进制主线的演进空间。

## 文本同构格式的真实价值

文本同构格式不是没有价值，但它的价值主要集中在“人类可读性”而不是“主链路运行价值”。

### 主要价值

1. 更容易做开发期调试和问题现场分析。
2. 更容易撰写协议设计文档、示例、评审材料。
3. 更容易表达最小失败用例和回归测试输入。
4. 更容易实现离线 inspect / pretty-print / 可视化工具。

### 不是刚需的地方

1. 它不是验证二进制结构正确性的必要条件。
2. 当前单元测试已经可以直接断言 wire bytes 和结构语义。
3. 它不会提升生产 replay / commit / version chain 回放的核心价值。

## 当前不引入正式文本序列化的原因

### 1. 最低公共抽象会拖累二进制实现

当前二进制实现已经开始出现适合专门优化的形态：

1. `ref struct BinaryDiffReader`
2. 基于 `ReadOnlySpan<byte>` 的无分配 cursor 推进
3. 直接围绕 `BinaryPrimitives`、`VarInt`、little-endian payload 做定制化实现

如果为了兼容文本 reader/writer 而过早引入接口化、多态分发、或统一 token reader 抽象，最先受损的往往就是这些低层专门优化。

### 2. 文本格式的复杂性并不只在“替换读写调用点”

真正复杂的问题通常出现在语义层，而不是字节层：

1. 数字的 canonical 文本形式是什么。
2. `-0.0`、`NaN`、`Infinity` 怎么表示。
3. 字符串、转义、Unicode 边界怎么定义。
4. 容器边界、分隔符、空白字符、注释是否支持。
5. 错误定位与错误恢复策略如何设计。

因此，“只是把 `BinaryPrimitives` 读写替换掉”只覆盖了最表面的部分，真正的文本协议设计远不止这一层。

### 3. 双格式并行最容易产生语义漂移

一旦 binary 和 text 都成为正式、可 roundtrip 的输入输出格式，就等于同时维护两套前端。

长期风险通常不是单点 bug，而是：

1. binary 先支持了新结构，text 没跟上。
2. text 允许的表示变体比 binary 多，造成不一致。
3. 文档、测试、错误处理在两条路径上逐渐漂移。

对于当前仍在快速迭代的 `StateJournal`，这是不必要的维护负担。

## 当前推荐替代方案：诊断表示，而不是正式文本格式

如果当前确实需要“人能看懂”的形式，优先级最高的不是文本 serializer/parser，而是：

1. binary -> diagnostic notation
2. binary -> pretty printer / inspector output

这种路线能获得大部分调试与分析收益，同时避免把“文本格式也必须是正式协议成员”的复杂性引入到底层读写架构中。

### 诊断表示的好处

1. 不需要承诺长期稳定的文本 wire contract。
2. 不要求文本 parser 与 binary parser 永远同构。
3. 不会迫使底层 Reader/Writer 走统一抽象。
4. 更适合做日志、调试器、离线分析工具和文档示例。

### 诊断表示的定位

诊断表示应该被视为：

1. 面向开发者的观察层
2. 不是正式序列化协议层
3. 可以演进、可以美化、可以为可读性而牺牲部分机械同构性

## 如果未来真的要支持文本格式，推荐的做法

如果未来确实出现强需求，例如：

1. 需要手写 fixture
2. 需要人工编辑 diff
3. 需要跨工具链交换一种更易读的中间表示

那么也不推荐直接把当前 binary 读写内核抽象成“大一统 dual-backend 框架”。

更稳妥的路线是：

### 方案：在更高层共享语义，而不是在最低层共享读写 API

1. 保留 binary reader/writer 的专门实现。
2. 在更高一层抽象“结构事件”或“语义节点”。
3. 让二进制格式和文本格式分别映射到这层语义结构。

这样可以同时做到：

1. 二进制实现继续保持性能特化。
2. 文本实现可以为可读性单独设计语法。
3. 共享的是协议语义，而不是共享一套会拖累所有后端的最低层接口。
