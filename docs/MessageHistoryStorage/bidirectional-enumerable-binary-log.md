# Message History Storage (Draft)
用于Raw LLM Message的存储

## 第0层 双向遍历与二进制分帧
Bidirectional-Enumerable Binary Log
简单说就是Magic开头，载荷两端都记录长度，载荷补0到4字节对齐，CRC32C封尾

每条Record定义为如下序列：Magic | EnveLen | Envelope | EnvePad | EnveLen | CRC32C\
多条紧密排列的Record序列构成Body：Record | Record | ... | Record

Magic: 4 字节固定常量"ELOG"，用于记录同步。固定值，简化设计。
EnveLen: uint32 LE，Envelope的字节长度。前后双写用于高效双向遍历。
Envelope：向上层承载的数据载荷。
EnvePad：在Envelope的尾部补0~3个字节的0，以实现4字节对齐。
CRC32C：**不包含头部的EnveLen**，为流式数据写入再回填创造机会。连续覆盖Envelope、EnvePad、尾部的EnveLen。固定hash算法，简化设计。

### 设计原则
本设计遵循以下核心原则：

## 分层重构提案（2025-08 共识版）
本节是在前述初版 API 讨论基础上的收敛与重构：我们将 **协议算法（分帧 + 双向寻址）** 与 **I/O / 缓冲 / 异步策略** 彻底解耦，确立“核心极简、包装分层”的演进路线，降低耦合与实现风险，提高可测试性与长期可维护性。

### 设计回顾与重构动机
初版方案中，`BinaryLogWriter` 同时承担：
1. 分帧协议（Magic / 双写长度 / Pad / CRC32C）
2. 流式写入（Seek / 非 Seek）
3. 占位与回填（长度 / CRC）
4. I/O 适配（文件 / 网络）
5. 可选缓冲策略（内存回退）

过多职责集中在单类型内会带来：
- 代码复杂度膨胀、测试粒度粗；
- 非 Seek 场景与高级占位需求互相牵制；
- 未来扩展（镜像修复 / 分片 / 广播写）需要改动核心；
- 难以在纯内存环境快速做属性测试（fuzz / 破损注入）。

### 分层结构总览
```
┌──────────────────────────────────────────────┐
│ 便捷/包装层 (High-Level Facades)             │
│  - BinaryLogFileAppender                     │
│  - NetworkBufferedWriter / Async Wrapper     │
│  - BinaryLog (静态枚举 API)                  │
├──────────────────────────────────────────────┤
│ 适配层 (Adaptation Layer)                    │
│  - IEnvelopeSink (抽象写入终点)              │
│  - SeekableStreamSink / MemorySink           │
│  - BufferedStreamingSink (非 Seek 回退)      │
├──────────────────────────────────────────────┤
│ 核心层 (Core Framing & Navigation)           │
│  - RecordFramer (ref struct, 驱动 IReservableBufferWriter) │
│  - FrameNavigator / RecordParser             │
│  - BinaryLogFormat / Crc32CAdapter           │
└──────────────────────────────────────────────┘
```

核心层只做：基于 `IReservableBufferWriter` 把一条 **Record** 的协议字段写入任意可扩展缓冲；或在只读内存/随机访问字节序列上解析与双向遍历。其生命周期限定在栈帧内（`ref struct`），无 I/O、副作用纯净，易 fuzz / property test。

### 核心类型概要
#### RecordFramer（ref struct）
职责：驱动一条 Record 的分帧协议，将写入请求转译到调用方提供的 `IReservableBufferWriter`；内部仅维护协议状态（Magic、长度占位、CRC）。

核心状态：
- `_writer`：遵循 `IReservableBufferWriter` 的后端（由调用方注入，推荐使用 `ChunkedReservableWriter` 等原生支持 reservation 语义的实现；若底层只有普通 `IBufferWriter<byte>`，需在适配层显式补齐 reservation/flush 语义，而非简单一层包装）
- `_crc`：增量 CRC32C 状态
- `_envelopeReservation`：头部长度占位的 reservation token
- `_writtenEnvelopeLength`：Envelope 已写入的字节数（供 pad/校验使用）

最小 API（示意）：
```csharp
public ref struct RecordFramer
{
    public RecordFramer(IReservableBufferWriter writer, int? envelopeLengthHint = null);
    public void BeginEnvelope();                         // 写 Magic，并通过 ReserveSpan 预留头长
    public Span<byte> GetSpan(int sizeHint);             // 透传到 writer.GetSpan，配合 Advance
    public void Advance(int count);                      // 更新已写长度与 CRC
    public Span<byte> ReserveSpan(int size, out int token, string? tag = null);
    public void Commit(int token);                       // 显式提交占位区，解除 flush 阻塞
    public void EndEnvelope();                           // 计算 pad，写尾长，写 CRC，并回填头长 reservation
    public int RecordLength { get; }                     // 返回本条记录的总长度（含补齐与 CRC）
}
```

**调用约定补充**
- `ReserveSpan` 会立即把指定长度纳入 Envelope 长度累计，调用方不应再对同一段调用 `Advance`；只需在填充完毕后执行 `Commit`。此处的“计入”指 `RecordFramer` 内部维护的 `_writtenEnvelopeLength`，与底层 `IReservableBufferWriter` 的实现无关；底层 writer 只负责提供可写缓冲与 flush 次序控制。`ChunkedReservableWriter` 通过为每个 reservation 锁定所属 chunk，确保后续的 `GetSpan`/`ReserveSpan` 不会迁移已预留区域，因此可以在写入其他内容后再回填该 `Span`；但一旦 `Commit`、`Reset` 或 `Dispose`，原始 `Span` 就不应再被使用。
- `GetSpan` 返回的缓冲在下一次请求或显式 `Advance(0)` 后即视为失效；`ReserveSpan` 返回的 `Span<byte>` 在 reservation 活跃期间保持有效，但若 writer 进入 `Reset`、被释放，或调用方完成 `Commit` 导致区域落盘，就必须停止访问该 `Span`。
- `RecordFramer` 默认启用严格校验：如果调用者在上一段 `GetSpan`（或 `GetMemory`）之后尚未调用 `Advance` 就再次索取缓冲，会立即抛出异常；若需要放弃先前缓冲，可显式调用 `Advance(0)` 释放租借。
- `EndEnvelope` 会验证所有 reservation 均已 `Commit`，否则抛出包含 `tag` 的诊断异常，确保写入前缀的完整性。
- **线程模型**：`RecordFramer` 与绝大多数 `IReservableBufferWriter` 实现仅支持单线程顺序写入。Reserve、Advance、Commit 以及 End 必须由同一调用线程按 FIFO 顺序执行；若需要跨线程协作，应在更高层通过消息队列或同步原语串联调用，而不是在核心分帧层尝试并发访问。
- **同步栈约束**：一条 Record 的写入必须在单个同步调用栈内完成；若调用方需要异步写入或跨线程拆分，应先在同步路径内完成分帧，再由外层按需封装异步发送逻辑。

**RecordFramer 生命周期状态表（衔接 `IReservableBufferWriter` 语义）：**

| 阶段 | 可调用成员 | 说明 |
| --- | --- | --- |
| 初始/空闲 | `BeginEnvelope()` | 进入记录写入流程并写入 Magic/头部占位；禁止重复调用。 |
| 已 Begin、无未提交的普通缓冲 | `GetSpan()` / `GetMemory()`；`ReserveSpan()` | 需要遵守接口层约定：如果选择 `GetSpan`/`GetMemory`，必须先 `Advance` 后才能继续；若选择 `ReserveSpan`，要求当前不存在“待 Advance”的普通缓冲。 |
| 待 Advance | `Advance()`（可传 0 退出） | 完成后回到“已 Begin、无未提交的普通缓冲”阶段，可继续请求新的缓冲或创建 reservation。 |
| 持有未提交 reservation | `ReserveSpan()`（嵌套排队）、`Commit(token)` | 允许同时存在多段 reservation，但必须按 token 调用 `Commit`；`EndEnvelope` 前必须全部提交。 |
| 准备结束 | `EndEnvelope()` | 验证所有 reservation 均已提交，计算 Pad、写尾长和 CRC，并通知底层 writer；调用后回到初始状态。 |

RecordFramer 不再直接管理裸 `Span` 缓冲；扩容与分片完全交由 `IReservableBufferWriter` 实现（例如 `ChunkedReservableWriter` 可按需租借 ArrayPool chunk）。

#### `IReservableBufferWriter` 交互约定
- **单线程语义**：实例仅支持单生产者顺序写入，所有 `GetSpan`/`ReserveSpan`/`Advance`/`Commit` 调用必须在同一线程上依次执行；`GetSpan` 返回的 buffer 会在下一次请求或执行 `Advance(0)` 时失效。
- **基本调用流程**：`GetSpan` → 写入 → `Advance`。`IReservableBufferWriter` 默认执行严格的顺序验证：若在 `Advance` 前再次索取 buffer（或调用 `ReserveSpan`），会立即抛出异常，有助于定位序列化器误用。若不再需要之前的缓冲，可调用 `Advance(0)` 主动放弃。
- **Reservation 生命周期**：`ReserveSpan` 立即切出固定区域供回填，调用方需在适当时机写入并调用 `Commit(token)`。未提交的 reservation 会阻挡前缀 flush；虽然 `Commit` 可乱序执行，但只有所有更早的 reservation 都提交后，数据才会向下游 writer 推进。
- **Flush 策略**：是否在 `Commit` 当下触发向下游写入由具体实现决定。`ChunkedReservableWriter` 会在连续前缀全部提交时立即复制到 `_innerWriter`，而面向磁盘或网络的自定义 writer 可以选择延迟到显式 `Flush`/`Dispose` 才落盘，只要能保证记录的前缀顺序不被破坏。文档默认假设调用方允许存在这种延迟，因此上层若需要强一致刷新，必须在关键位置显式调用 sink 的 `Flush`（或在更高层自备异步封装）。
- **Envelope 长度与 CRC**：Envelope 的长度统计与 CRC 计算完全由 `RecordFramer` 负责；`IReservableBufferWriter` 不感知“Envelope”概念。`RecordFramer` 在调用 `ReserveSpan` 时会增加 `_writtenEnvelopeLength` 并记录等待在 `Commit` 时纳入 CRC 的区段，因此实现自定义 writer 时无需维护额外的 Envelope 级别计数。
- **结束守卫**：`RecordFramer.EndEnvelope`、`ChunkedReservableWriter.Reset/Dispose` 等都会检查是否仍有未提交的 reservation，若存在则抛出 `InvalidOperationException` 并包含 `tag` 信息。推荐在调试阶段为关键 reservation 设置 tag，结合 `DebugUtil.Print` 快速定位遗漏。
- **调试建议**：`ChunkedReservableWriterOptions.DebugLog` 可直接绑定 `DebugUtil.Print`（或其它回调），内部会在 `Advance`/`ReserveSpan`/`Commit`/flush 等关键路径输出调试信息；若实现自定义 `IReservableBufferWriter`，也建议提供类似钩子以便统一排障。
- **适配普通 IBufferWriter**：除非能完整复现 reservation/commit 的阻塞语义，否则不要直接“轻量包装”普通 `IBufferWriter<byte>`；推荐在适配层实例化 `ChunkedReservableWriter` 承接写入，再将已提交前缀批量推送到底层 `IBufferWriter`。

**实现者注意事项（Checklist）**
- `ReserveSpan` 必须为每个 token 维持独立状态，并阻止旧 token 未提交时的数据 flush；重复 `Commit` 或未知 token 应抛出异常。
- `Commit` 成功后要更新“连续前缀已提交”状态：若实现选择即时 flush，应立即把可写区推送到底层；若实现选择延迟 flush，也必须保证后续 `Flush` 能据此状态一次性输出正确的连续前缀。
- `Reset`/`Dispose` 需确保归还所有租借缓冲，并在存在未提交 reservation 时提供显式诊断（抛异常或记录日志）。
- 若实现涉及内部扩容，必须确保此前发出的 `Span`/`Memory` 不会跨 chunk，避免调用方 `Advance` 时写越界。
- `ReserveSpan` 返回的内存块在对应 reservation `Commit`、实现的 `Reset` 或 `Dispose` 之前不得搬移或重定位；实现若需要扩容，必须保留原地址直至生命周期结束。
- 若实现选择延迟 flush，需要维护“连续前缀水位”，确保显式调用 `Flush()` 时按 FIFO 顺序输出所有已提交的前缀数据，不得跳跃或重排。

#### CRC32C 适配层
- `RecordFramer` 并不直接依赖 `System.IO.Hashing.Crc32C`，而是通过一个最小化的接口（草案命名为 `ICrc32C`）来追加数据、获取 hash，并在 `Reset` 时回收对象。
- 默认实现 `DefaultCrc32CAdapter` 只是对 `System.IO.Hashing.Crc32C` 的薄包装，便于在 .NET 7+/8+ 上零成本启用硬件加速；但在单元测试或实验场景下，可替换为“空实现”“自定义校验算法”或“记录 Append 调用序列”的适配器，用于 fuzz、对拍等工作流。
- 通过构造 `RecordFramer` 时注入 CRC 适配器，可以在不修改核心分帧逻辑的前提下扩展出“CRC 关闭”“强校验”“双写核对”等策略，同时也避免在热路径上产生不必要的分配。

#### Flush 顺序示例

以下片段演示 reservation 的阻塞行为；只有最早的 reservation 被 `Commit` 后，前缀数据才会 flush 到下游 `innerWriter`。

```csharp
using System.Buffers.Binary;

var inner = new ArrayBufferWriter<byte>();
var writer = new ChunkedReservableWriter(inner, new ChunkedReservableWriterOptions {
    DebugLog = Atelia.Diagnostics.DebugUtil.Print,
    DebugCategory = "BinaryLog"
});

// 1. 预留 4 字节用于稍后写入长度
var header = writer.ReserveSpan(4, out var headerToken, tag: "length");

// 2. 写入实际 payload（顺序写 + Advance）
var payload = writer.GetSpan(10);
payload.Fill(0x42);
writer.Advance(10);

// 此时 header 未提交，FlushCommittedData 不会把任何字节推给 inner。
Debug.Assert(writer.PendingReservationCount == 1);
Debug.Assert(inner.WrittenCount == 0);

// 3. 回填 header 并 Commit，触发前缀 flush
BinaryPrimitives.WriteInt32LittleEndian(header, 10);
writer.Commit(headerToken);

// Commit 后 flush 发生，innerWriter 立即拿到完整的 14 字节（4 + 10）。
Debug.Assert(writer.PendingReservationCount == 0);
Debug.Assert(inner.WrittenCount == 14);
```

#### FrameNavigator / RecordParser
针对只读数据：
```csharp
public ref struct FrameNavigator
{
    public FrameNavigator(ReadOnlySpan<byte> data, bool startFromEnd = true);
    public bool TryReadCurrent(out ReadOnlySpan<byte> envelope, bool verifyCrc = true);
    public bool TryMovePrevious(); // 依赖尾长反向跳
    public bool TryMoveNext();     // 依赖头长正向跳
    public BinaryLogErrorCode? LastError { get; }
}
```
在损坏/不完整尾部场景：
- 初始构造阶段只做“边界最小闭合”判定；
- CRC 校验按调用者需要开启；
- 反向移动使用：`tailLenPos = curStart - 8` 公式 + Magic 校验。

### 错误模型（核心层）
核心只抛以下异常（或通过状态暴露）：
- `UnsupportedFormat`：Magic 不匹配；
- `LengthMismatch`：头尾长度不一致（解析时）；
- `CrcMismatch`：在 verifyCrc = true 时发现；
- `IncompleteTail`：尾部缺失尾长或 CRC；
- `OutOfRange`：越界访问（输入损坏导致）。

写入路径中大部分错误以 `ArgumentException` / `InvalidOperationException` 体现（例如二次 Begin / 未 Begin 调用 End / 缓冲不足且未提供扩展）。

### I/O 适配层接口
包装层负责把“已分帧的 Record” 推向某个终端，同时提供在未知长度/非 Seek 场景下的缓冲支撑：

```csharp
public interface IEnvelopeSink
{
    // 一次推送一条完整 Record；实现可立即落盘或排队，由实现自行决定是否额外封装异步逻辑
    void WriteRecord(ReadOnlySpan<byte> record);

    // 可选：提供 reservable writer，便于 RecordFramer 直接写入并延迟 commit
    IReservableBufferWriter? TryCreateReservableWriter(int? sizeHint = null);

    // 可选：显式刷新，在非 Seek 场景推动底层写入；默认实现可为空实现
    void Flush();
}
```

典型实现：
- `SeekableStreamSink`：`WriteRecord*` 直接写入；若调用 `TryCreateReservableWriter`，返回一个对底层 stream 做 seek/回填的 `EnvelopeScope`。
- `BufferedStreamingSink`：针对非 Seek/网络场景，内部实例化 `ChunkedReservableWriter`（ArrayPool-backed）承接 RecordFramer 写入，所有阻塞 reservation 提交后触发同步 flush，并实现 `Flush` 以便显式冲刷；如需异步落盘，由外层自行包装。
- `MemorySink`：返回聚合到 `List<byte>` 或 `IMemoryOwner<byte>` 的 writer，用于测试和属性验证。

### 高级便捷包装（Facade）
`BinaryLogFileAppender`：
```csharp
public sealed class BinaryLogFileAppender
{
    private readonly Stream _stream;
    private readonly byte[] _scratch; // 小型重用缓冲
    public void AppendEnvelope(ReadOnlySpan<byte> envelope); // 内部构建 RecordFramer
}
```
`BinaryLog`（静态枚举）：基于 `FrameNavigator` 提供 `IEnumerable<ReadOnlyMemory<byte>> ReadBackward(...)` 等高层 API（当前文档已有，迁移内部实现到新解析器）。

### 同步 vs 异步策略
核心 `RecordFramer` 永远同步（栈上，单函数帧内完成）。如果调用方需要异步写入或网络背压，应在更高层先通过 `ChunkedReservableWriter` 或自定义缓冲把记录构建完毕，再由外层控制异步发送。
优点：
- 避免在核心层引入 `await` 导致的状态机与逃逸；
- 背压与异步行为完全交由 sink 或调用方定制，不干扰协议层；
- 非 Seek/未知长度场景下，可借助 `ChunkedReservableWriter` 将 Record 写入 ArrayPool chunk，待 Commit 后一次性 flush 到真实终端；
- 测试核心算法无需异步基建；
- 允许极致场景（内存拼装 → 多播 N 个 sink）。

### 典型使用示例
#### 1) 已知长度一次性写入
```csharp
var writer = new BinaryLogWriter(stream);
writer.WriteEnvelope(envelopeBytes); // 内部自动写 Magic/长度/Pad/CRC
```

#### 2) 作用域写入 + reservation 回填
```csharp
var writer = new BinaryLogWriter(stream);
using (var scope = writer.BeginEnvelope()) // BeginEnvelope 返回实现 IReservableBufferWriter 的作用域
{
    // 预留头部字段，在稍后填写
    var headerSpan = scope.ReserveSpan(8, out var headerToken, tag: "header");

    // 写入正文 payload
    var payloadSpan = scope.GetSpan(payload.Length);
    payload.CopyTo(payloadSpan);
    scope.Advance(payload.Length);

    // 回填头部并提交 reservation
    BinaryPrimitives.WriteInt32LittleEndian(headerSpan, version);
    BinaryPrimitives.WriteInt32LittleEndian(headerSpan[4..], flags);
    scope.Commit(headerToken);
} // Dispose 自动补齐 pad、尾长，并回填头部长度
```

#### 3) 非 Seek 流 + `ChunkedReservableWriter`
```csharp
IEnvelopeSink sink = new BufferedStreamingSink(networkStream);
// SinkBackpressureWriter: 小型适配器，将 sink 的 WriteRecord* 包装成 IBufferWriter<byte>
IReservableBufferWriter writer = sink.TryCreateReservableWriter()
    ?? new ChunkedReservableWriter(
        new SinkBackpressureWriter(sink),
        new ChunkedReservableWriterOptions {
            DebugLog = Atelia.Diagnostics.DebugUtil.Print,
            DebugCategory = "BinaryLog"
        });

var framer = new RecordFramer(writer);
framer.BeginEnvelope();

int flags = ComputeFlags(payload);
var headerSpan = framer.ReserveSpan(4, out var headerToken, tag: "flags");
var bodySpan = framer.GetSpan(payload.Length);
payload.CopyTo(bodySpan);
framer.Advance(payload.Length);
BinaryPrimitives.WriteInt32LittleEndian(headerSpan, flags);
framer.Commit(headerToken);

framer.EndEnvelope();
sink.Flush(); // 由 sink 决定具体 flush 策略
```

#### 4) 解析并反向遍历
```csharp
ReadOnlySpan<byte> fileBytes = mmapSpan; // 或一次性读取
var nav = new FrameNavigator(fileBytes, startFromEnd: true);
while (nav.TryReadCurrent(out var env, verifyCrc: true))
{
    Process(env);
    if (!nav.TryMovePrevious()) break;
}
```

### 扩展点与未来工作
| 领域 | 形式 | 是否侵入核心 |
|------|------|--------------|
| 尾部损坏检查/截断 | `TailInspector` | 否（使用 FrameNavigator 重用解析逻辑） |
| 镜像文件比较/修复 | `MirrorRepairTool` | 否 |
| 分片策略 (Roll / Segment) | 外部调度器 | 否 |
| 并发写入 (单生产者→多 sink) | 组合 sink | 否 |
| 压缩/加密 Envelope | 上层 Envelope 序列化 | 否 |

### 迁移/命名映射
| 初版概念 | 新版对应 | 说明 |
|----------|----------|------|
| BinaryLogWriter (含 I/O) | RecordFramer + EnvelopeSink | 写入协议与 I/O 解耦 |
| EnvelopeScope | RecordFramer (Begin/End) | 作用域语义保留但简化 |
| BinaryLogReader | FrameNavigator | 双向 + 可选 CRC 校验 |
| AllowIncompleteTail | 构造参数/Inspector | 不放进核心写入路径 |
| Repair 组件 | TailInspector / MirrorRepairTool | 独立工具 |

### 质量与测试策略
核心属性测试建议：
- 任意随机 envelope 字节（含 0 长度）→ 构建 Record → 解析 → 校验内容一致。
- 破坏尾部 CRC / 尾长 / Magic → Parser 应给出对应错误码（或 false + LastError）。
- 反向遍历与正向遍历产生的 envelope 集合逆序一致。
- Fuzz：在 Record 任意注入随机字节翻转 N% 后尝试重同步；验证不会越界崩溃。
- `IReservableBufferWriter` 语义：多段 `ReserveSpan` / `Commit`，确保未提交 reservation 阻止 flush；模拟丢失 `Commit` 时 `EnvelopeScope.Dispose` 抛出；验证乱序 `Commit`、重复 `Commit` 的防御，并确认 flush 始终遵循 FIFO 前缀。

### 分工（执行计划占位）
- GPT-5：本章节主笔 + 后续统一术语（已完成初稿）。
- Claude：补充 `RecordFramer` 伪实现精化（增长策略 / CRC 适配器接口）。
- Gemini：完善包装层“同步 vs 异步”准则与典型 sink 组合策略。
- Maintainer（你）：审阅裁剪，确认是否需要增加版本字段 / 兼容标签。

### 下一步实施建议（Incremental）
1. 实现 `BinaryLogFormat` 常量 + `Crc32CAdapter`（接口 + System.IO.Hashing 封装）。
2. 实现 `RecordFramer` MVP（不含自动扩容，要求外部提供足够缓冲）。
3. 实现 `FrameNavigator`（正向 + 反向 + 最小校验 + 可选 CRC）。
4. 添加最小单测：构建/解析/双向一致性 + CRC 破坏用例。
5. 提供 `SeekableStreamSink` + `BinaryLogFileAppender` 快速落盘。
6. 扩展：BufferedStreamingSink（可选）。
7. 之后再切入 Repair / Mirror / Segment 工具。

---
（本章节为演进提案，后续如实现细节与最初文档早期段落存在轻微不一致，以本节描述的“分层架构”作为后续代码实现基准。）

### API与关键实现示意
以下列出主要类型签名与少量关键逻辑（.NET 7+/8+，使用 System.IO.Hashing.Crc32C）。本版以 `IReservableBufferWriter` 为核心写入接口，并引入 BinaryLogCursor 以支持正/反向读取。

```csharp
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

// 常量与格式约定（确定）
// Record = Magic(4) | EnveLen(4, LE) | Envelope | EnvePad(0~3) | EnveLen(4, LE) | CRC32C(4, LE)
// CRC32C 覆盖: Envelope + EnvePad + 尾部的 EnveLen；不含头部的 EnveLen 与 Magic。
internal static class BinaryLogFormat
{
    public const uint MAGIC = 0x474F4C45u; // 'E''L''O''G' (LE)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int PaddingOf4(uint len) => (4 - (int)(len & 3)) & 3;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AlignedLength4(uint len) => (len + 3u) & ~3u; // 等价于 len + (uint)PaddingOf4(len)
}

// 写入器（对外稳定 API）
public sealed class BinaryLogWriter
{
    // 构造两种底座：
    public BinaryLogWriter(Stream stream);
    public BinaryLogWriter(IEnvelopeSink sink);
    public BinaryLogWriter(IReservableBufferWriter writer);

    // 最常用：一次性写完整 Envelope
    public void WriteEnvelope(ReadOnlySpan<byte> envelope);

    // 作用域写入（可流式）：返回 EnvelopeScope（IReservableBufferWriter），结束时自动补齐并写入尾长与 CRC32C
    public EnvelopeScope BeginEnvelope(int knownLength = -1);

    // 内部/测试可见：
    internal bool CanSeek { get; }
}

// Envelope 作用域（确定 using/Dispose 触发 End）
// 自身不再维护协议状态，仅把 RecordFramer 暴露为 IReservableBufferWriter 供调用方使用，并在 Dispose 阶段完成 flush。
public readonly ref struct EnvelopeScope
{
    private readonly RecordFramer _framer;
    private readonly IEnvelopeSink _sink;
    private readonly bool _flushOnDispose;
    private readonly bool _seekable;
    private readonly Action<RecordWriteResult>? _onCompleted;

    public EnvelopeScope(RecordFramer framer, IEnvelopeSink sink, bool flushOnDispose, bool seekable, Action<RecordWriteResult>? onCompleted = null);

    // 底层写入：简单转发到 RecordFramer
    public Span<byte> GetSpan(int sizeHint = 0) => _framer.GetSpan(sizeHint);
    public void Advance(int count) => _framer.Advance(count);
    public Span<byte> ReserveSpan(int count, out int reservationToken, string? tag = null)
        => _framer.ReserveSpan(count, out reservationToken, tag);
    public void Commit(int reservationToken) => _framer.Commit(reservationToken);

    // 结束：调用 RecordFramer 完成协议写入，随后交由 sink 处理 flush/seek 回填
    public void Dispose()
    {
        var result = _framer.CompleteEnvelope();
        _onCompleted?.Invoke(result);
        if (_flushOnDispose) { _sink.Flush(); }
    }
}

若 `CompleteEnvelope` 时检测到仍有未提交的 reservation，会抛出 `InvalidOperationException` 并拒绝落盘，以保障写入的连续前缀安全。

> 说明：所有协议字段（Magic/长度/Pad/CRC）都集中在 `RecordFramer` 内部维护；`EnvelopeScope` 仅负责生命周期与 sink 通知，并且只提供同步 `Dispose`。如需自定义写入策略，请直接组合或扩展 `RecordFramer`，而不是在作用域包装层重复实现协议逻辑；若调用方需要异步 flush，应在外层自备包装。

// 低级API：面向高级用户的精确控制接口
// 注：BinaryLogReader 不实现 IDisposable，不承担 Stream 的所有权和关闭义务
// 调用者负责管理底层 Stream 的生命周期
public sealed class BinaryLogReader
{
    // 底层来源（文件/内存）
    // startFromEnd: true=从末尾开始(默认，用于读取最新消息), false=从开头开始
    public BinaryLogReader(Stream stream, bool startFromEnd = true);
    public BinaryLogReader(ReadOnlyMemory<byte> data, bool startFromEnd = true);

    // 获取当前记录的 Envelope：默认执行 CRC 校验
    // 注意：返回的 ReadOnlyMemory 指向内部缓冲区，在下一次移动操作前有效
    // 如需长期持有数据，请调用 .ToArray() 或使用 TryReadCurrentOwned
    public bool TryReadCurrent(out ReadOnlyMemory<byte> envelope, bool verifyCrc = true);

    // 获取当前记录的 Envelope 并返回独立副本（调用方拥有数据所有权）
    // public bool TryReadCurrentOwned(out byte[] envelope, bool verifyCrc = true); 高级接口中的简化包装，先不做

    // 流式读取当前记录（适用于超大 Envelope，避免内存分配）
    // 返回的 Stream 在下一次移动操作前有效，支持 Seek 和 Length
    public bool TryOpenCurrentStream(out Stream envelopeStream, bool verifyCrc = true);

    // 仅通过 Magic + EnveLen 进行定位（快速移动，不做 CRC）
    // 注意：这些操作会改变 Reader 的内部状态
    public bool TryMoveNext();
    public bool TryMovePrevious();



    // 元信息（便于恢复/截断）
    public long CurrentOffset { get; }
    public int CurrentLength { get; }
    public bool IncompleteTailDetected { get; }
}

// 高级API：面向普通用户的无脑接口
public static class BinaryLog
{
    // 直接从数据源创建迭代器，内部管理所有状态
    // 每次调用都会创建新的 Reader 实例，避免状态共享问题
    public static IEnumerable<ReadOnlyMemory<byte>> ReadForward(Stream stream, bool verifyCrc = true);
    public static IEnumerable<ReadOnlyMemory<byte>> ReadBackward(Stream stream, bool verifyCrc = true);
    public static IEnumerable<ReadOnlyMemory<byte>> ReadForward(ReadOnlyMemory<byte> data, bool verifyCrc = true);
    public static IEnumerable<ReadOnlyMemory<byte>> ReadBackward(ReadOnlyMemory<byte> data, bool verifyCrc = true);
}

// 实现示例（内部逻辑）：
/*
public static IEnumerable<ReadOnlyMemory<byte>> ReadBackward(Stream stream, bool verifyCrc = true)
{
    var reader = new BinaryLogReader(stream, startFromEnd: true);
    while (reader.TryReadCurrent(out var envelope, verifyCrc))
    {
        yield return envelope;
        if (!reader.TryMovePrevious()) break;
    }
}
*/

// 关键逻辑片段（伪代码/示意）
// BeginEnvelope/Dispose 路径（围绕 RecordFramer）
/*
BinaryLogWriter.BeginEnvelope(int knownLength)
{
    WriteUInt32LE(MAGIC);
    WriteUInt32LE(knownLength >= 0 ? (uint)knownLength : 0u);

    var writer = _sink.TryCreateReservableWriter(knownLength >= 0 ? knownLength : null)
        ?? _chunkedFallback; // 缺省回退到内部维护的 ChunkedReservableWriter

    var framer = new RecordFramer(writer, envelopeLengthHint: knownLength >= 0 ? knownLength : null);
    framer.BeginEnvelope();
    return new EnvelopeScope(
        framer,
        _sink,
        flushOnDispose: true,
        seekable: _canSeek,
        onCompleted: result => {
            if (result.CanPatchHeader && _canSeek) {
                FillPlaceholder32(result.HeaderReservationToken, result.EnvelopeLength);
            }
        });
}

EnvelopeScope.Dispose() // End
{
    // 记录所有协议字段写入由 RecordFramer.CompleteEnvelope 完成；
    // EnvelopeScope.Dispose 仅负责调用 sink.Flush（若需要），并根据回调执行 seek 回填。
}
*/

// 一次性写入快路径（确定）
/*
BinaryLogWriter.WriteEnvelope(ReadOnlySpan<byte> env)
{
    WriteUInt32LE(MAGIC);
    WriteUInt32LE((uint)env.Length);

    var crc = new Crc32C();
    AppendAndWrite(env, ref crc);

    int pad = BinaryLogFormat.PaddingOf4((uint)env.Length);
    WriteZeros(pad);

    // 尾长参与 CRC
    Span<byte> lenLE = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(lenLE, (uint)env.Length);
    crc.Append(lenLE);
    Write(lenLE);

    Span<byte> outHash = stackalloc byte[4];
    crc.GetCurrentHash(outHash);
    uint c = BinaryPrimitives.ReadUInt32LittleEndian(outHash);
    BinaryPrimitives.WriteUInt32LittleEndian(lenLE, c);
    Write(lenLE);
}
*/
```

- 非 seek 场景由 sink/适配层决定是否创建 `ChunkedReservableWriter` 作为缓冲回退，核心层只负责驱动协议。
- BeginEnvelope 对外仅暴露 `IReservableBufferWriter` 入口；如需 Stream 适配可选择性提供包装或桥接。
- Reader 的 TryReadCurrent 默认执行 CRC 校验；TryMoveNext/TryMovePrevious 仅依赖 Magic 与 EnveLen 进行快速移动。
- Reader 在检测到尾部不完整记录时应标记 IncompleteTailDetected，供调用方选择截断或忽略。

## API设计决策说明
**双层API设计**：为解决有状态对象共享导致的潜在问题，采用双层API设计：
- **高级API (`BinaryLog`)**：面向90%的普通用户，提供无脑的静态方法，内部自动管理所有状态，避免用户接触到有状态对象。
- **低级API (`BinaryLogReader`)**：面向10%的高级用户，提供精确的状态控制。用户通过手写循环配合TryReadCurrent/TryMoveNext等方法实现自定义遍历逻辑。

**命名决策**：
- `BinaryLogCursor` → `BinaryLogReader`：明确只读语义，避免与"游标"概念混淆。
- `EnumerateForward/Backward` → `ReadForward/Backward`：更直观的动词，符合.NET命名习惯。
- 扩展方法模式被放弃：虽然使用体验好，但会暴露有状态对象给普通用户，增加误用风险。

**基于外部审查的改进**：
- **内存生命周期明确化**：为 `TryReadCurrent` 添加了明确的生命周期说明，并提供 `TryReadCurrentOwned` 用于需要持久化数据的场景。
- **结构化异常处理**：定义了 `BinaryLogErrorCode` 枚举和 `BinaryLogException` 类型，提供精确的错误定位和处理能力。
- **非Seek流支持**：默认在内部使用 `ChunkedReservableWriter` 作为回退缓冲；如需异步写入或特定背压策略，由外层调用方自行封装。

## 打开选项与最小校验（更新）
为与“检查/修复”解耦，Reader/Writer 仅做最基本的快速帧校验：
- Writer 打开/追加时不主动修复；若发现起始定位处帧头无效或尾部未闭合（需定位到最后一条完整记录），默认抛 InvalidDataException。
- Reader 构造时做一次“最小校验”（定位到第一条或最后一条完整记录的边界）：
    - 默认行为：若尾部不完整或遇到明显损坏，抛 InvalidDataException（错误代码见下文）。
    - 可选放宽：提供 `allowIncompleteTail: bool = false` 开关；当为 true 时，不抛异常，但 `IncompleteTailDetected` 会置位，向前/向后枚举只遍历到最后一条完整记录为止。

建议（不改变上面的公开签名）：新增一个可选的打开参数对象以承载开关，而不污染主构造函数：
```csharp
public readonly struct BinaryLogOpenOptions
{
        public bool StartFromEnd { get; init; } // 默认 true
        public bool AllowIncompleteTail { get; init; } // 默认 false
        public bool MinimalValidateOnOpen { get; init; } // 默认 true
}
```
并补充一个便捷重载：`new BinaryLogReader(Stream, BinaryLogOpenOptions options)`。

备注：Open 阶段仅做“边界有效性”与“帧闭合性”检查，不做全文件 CRC 全检；更深入的健康检查交由下述独立组件完成。

## 完整性检查与修复（独立组件）
将数据健康检查与修复从正常读写路径分离，集中到一个工具型组件，便于部署前/启动时或故障现场使用。

目标与策略：
- 单文件尾部检查与修复：检测并安全截断未闭合的尾部记录，可选进行小范围重同步（仅限文件尾部窗口）。
- 双写镜像比对修复：对 primary/mirror 两个日志进行“公共前缀+尾部差异”比较，选择可信副本进行修复或回滚到公共前缀。

建议 API（草案，.NET 7/8）：
```csharp
public enum TailStatus { Clean, Empty, IncompleteTail, CorruptedTail }

public sealed class BinaryLogCheckAndRepair
{
        // 1) 单文件：仅检查，不修改（核心 Stream 实现）
        public static TailReport CheckTail(Stream stream, int resyncWindowBytes = 64 * 1024);
        // 便捷重载：基于文件路径
        public static TailReport CheckTail(string path, int resyncWindowBytes = 64 * 1024);

        // 2) 单文件：修复计划与执行（只做尾部修复，永不改写中间）
        public static RepairPlan PlanTailRepair(TailReport report, TailRepairPolicy policy = TailRepairPolicy.TruncateOnly);
        // 核心 Stream 实现
        public static RepairResult RepairTailInPlace(Stream stream, RepairPlan plan);
        // 便捷重载：基于文件路径
        public static RepairResult RepairTailInPlace(string path, RepairPlan plan);

        // 3) 双写镜像：比较与修复（核心 Stream 实现）
        public static MirrorReport CheckMirror(Stream primaryStream, Stream mirrorStream, int resyncWindowBytes = 64 * 1024);
        // 便捷重载：基于文件路径
        public static MirrorReport CheckMirror(string primaryPath, string mirrorPath, int resyncWindowBytes = 64 * 1024);
        public static MirrorRepairPlan PlanMirrorRepair(MirrorReport report, MirrorPolicy policy = MirrorPolicy.PreferLongerValidTail);
        // 核心 Stream 实现
        public static MirrorRepairResult RepairMirrorInPlace(Stream primaryStream, Stream mirrorStream, MirrorRepairPlan plan);
        // 便捷重载：基于文件路径
        public static MirrorRepairResult RepairMirrorInPlace(MirrorRepairPlan plan);
}

public enum TailRepairPolicy
{
        TruncateOnly,        // 仅截断到最后一条完整记录
        TruncateWithResync,  // 尾部窗口内尝试重同步后再截断（限定在 resyncWindowBytes）
}

public enum MirrorPolicy
{
        PreferLongerValidTail, // 优先选择拥有更长“有效尾部”的副本作为基准
        PreferCommonPrefix,    // 保守：双侧均截断到公共前缀，避免不一致数据传播
}
```
实现要点：
- **核心基于 Stream 实现**：所有检查和修复逻辑的核心实现都基于 `Stream` 接口，便于测试（可使用 `MemoryStream`）和提高灵活性。文件路径版本的重载内部通过打开 `FileStream` 调用核心 Stream 版本实现。
- **Tail 检查**：从文件尾向前探测 `| EnveLen | CRC32C |`，计算上溯起点，校验 Magic 与头长一致；若校验失败，在 `resyncWindowBytes` 范围内按字节回退重同步；超出窗口则判为 CorruptedTail。
- **Tail 修复**：仅做截断（`Stream.SetLength`），不改写中间内容；若策略含重同步，则先应用"在窗口内找到的下一条可信帧"的边界再截断。
- **镜像比对**：同时扫描两文件的公共前缀边界与各自最后一条完整记录；若分歧，依据策略：
    - PreferLongerValidTail：选取拥有更长有效尾部的副本，复制差异块或将另一侧截断至相同边界；
    - PreferCommonPrefix：将两侧均截断至公共前缀，确保一致性（最保守）。
- **并发与安全**：修复操作要求独占访问（可选加文件锁）；提供 dryRun 预演计划，便于审计/回滚。

## 位移公式与寻址（明确）
- 记 `len = EnveLen`，`pad = (4 - (len & 3)) & 3`，等价于 `pad = BinaryLogFormat.PaddingOf4(len)`；`aligned = BinaryLogFormat.AlignedLength4(len) = len + pad`。
- 前向步进：`next = pos + 4 /*Magic*/ + 4 /*HeadLen*/ + len + pad + 4 /*TailLen*/ + 4 /*CRC*/`。
- 反向定位：`tailLenPos = pos - 8`；`prevLen = u32LE(tailLenPos)`；`prevPad = BinaryLogFormat.PaddingOf4(prevLen)`；`start = tailLenPos - (8 + prevLen + prevPad)`，再校验 `[start] == MAGIC` 且 头长==`prevLen`（可选再做 CRC）。

## 错误分类与异常语义（明确）

### 错误码定义
```csharp
public enum BinaryLogErrorCode
{
    IncompleteTail,    // 末尾缺少尾长或 CRC，或 payload 未补齐；常见于异常中断
    LengthMismatch,    // 头尾长度不一致
    CrcMismatch,       // CRC32C 校验失败（Envelope+Pad+TailLen）
    MagicNotFound,     // 无法在期望位置找到 Magic
    OutOfRange,        // 访问越界
    WritebackFailed,   // Seek 回填操作失败
    UnsupportedFormat  // 格式版本不支持
}

public class BinaryLogException : InvalidDataException
{
    public BinaryLogErrorCode ErrorCode { get; }
    public long Offset { get; }

    public BinaryLogException(BinaryLogErrorCode errorCode, long offset, string message)
        : base(message)
    {
        ErrorCode = errorCode;
        Offset = offset;
    }
}
```

### 异常处理策略
Reader/Writer 默认在打开时执行“最小校验”，遇到上述任一错误即抛 `BinaryLogException`，并附带错误码/位置；若 `AllowIncompleteTail=true`，则仅置位 `IncompleteTailDetected` 并从最近的一条完整记录边界启读。

## 线程安全与缓冲建议（明确）
- BinaryLogWriter/BinaryLogReader 非线程安全；同一实例请避免并发访问。
- **BinaryLogReader 资源管理**：`BinaryLogReader` 不实现 `IDisposable`，不承担底层 `Stream` 的所有权。调用者负责管理 `Stream` 的生命周期，典型用法：
  ```csharp
  // 高级API：推荐用法，简单直接
  using (var stream = File.OpenRead("path/to/log.elog"))
  {
      foreach (var entry in BinaryLog.ReadBackward(stream))
      {
          // 处理 entry
      }
  } // stream 在此处被正确关闭

  // 低级API：高级用户精确控制
  using (var stream = File.OpenRead("path/to/log.elog"))
  {
      var reader = new BinaryLogReader(stream, startFromEnd: true);
      while (reader.TryReadCurrent(out var entry))
      {
          // 处理 entry
          if (!reader.TryMovePrevious()) break;
      }
  } // stream 在此处被正确关闭
  ```
- Writer 对 Stream 建议包裹缓冲（如 BufferedStream 或自管 ArrayPool<byte> 缓冲），减少小块写；对 `IReservableBufferWriter` 路径尽量批量 Append/Commit，避免频繁触发 flush。

## 边界与限制（明确）
- EnveLen 为 uint32（LE）：理论上最大 4 GiB-1；实践中建议配置上限（例如 1 GiB）以免误写造成内存压力。
- 允许零长度 Envelope（pad=0）；需要测试覆盖。
- CRC32C 仅用于错误检测而非抗篡改；加密/认证放在上层 Envelope。

## 实施里程碑建议
**Phase 1: 核心读写功能（MVP）**
- `BinaryLogFormat` 常量和工具函数
- `BinaryLogWriter` 基础写入功能（WriteEnvelope + BeginEnvelope/EnvelopeScope）
- `BinaryLogReader` 基础读取功能（TryReadCurrent + TryReadCurrentOwned + TryMoveNext/Previous）
- `BinaryLog` 高级API（ReadForward/ReadBackward）
- `BinaryLogException` 和 `BinaryLogErrorCode` 异常体系

**Phase 2: 性能和健壮性增强**
- `BinaryLogOpenOptions` 配置支持（若仍有必要）
- 流式读取API（`TryOpenCurrentStream`）用于大记录处理
- 完整的损坏检测和IncompleteTailDetected标记
- 非Seek流的内存缓冲支持。可选，首要用户是文件写入。

**Phase 3: 工具和修复**
- `BinaryLogCheckAndRepair` 完整实现
- 双写镜像支持
- 大文件性能优化

**Phase 4: 可扩展性（长期规划）**
- 日志分片（Segmentation）策略。不处理，交由外层用户完成。
- 并发写入优化（ConcurrentBinaryLogWriter）。不处理，单写者。
- 跨文件操作支持。不处理，单文件。

## 最小测试清单（建议）
- 基础：单条/多条/零长度，正向/反向一致；CRC 开启/关闭读取。
- 对齐：len=1..5 的 pad 正确；下一条 Magic 位于 4 字节边界。
- 尾部异常：缺 CRC、缺尾长、缺部分 payload，能检测并（通过工具）安全截断。
- 中间损坏：随机翻转若干字节，能靠重同步跳过损坏块并继续遍历。
- Seek/非Seek：非 Seek + 未知长度时，通过 `ChunkedReservableWriter` 缓冲并验证 flush 触发；Seek 路径能回填头长且不漏写。
- Reservable 契约：多 reservation、乱序 `Commit`、重复 `Commit`、提前 `Dispose` 等场景下，确认 `ChunkedReservableWriter` 与 `EnvelopeScope` 行为符合预期（未提交数据不会被 flush，释放后归还 ArrayPool chunk）。
- 大尺寸：>100MB 连续写读，内存占用与吞吐可接受。
