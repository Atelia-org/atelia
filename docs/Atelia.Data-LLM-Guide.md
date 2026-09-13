# Atelia.Data - LLM友好使用指南

> 迁移说明：Atelia.Data 已迁入 atelia-storage。本指南保留原写作时期的说明，以下组件版本与旧路径不代表当前包版本；请从[存储库依赖](storage-dependency.md)按 `StorageSourceRevision` 定位对应源码及现行文档。

**创建时间**: 2025-08-26 08:00
**目标读者**: LLM Agent和开发者
**组件版本**: v1.0 (稳定版本)

---

## 🎯 **核心概念**

### **什么是ChunkedReservableWriter？**
ChunkedReservableWriter是一个高性能的缓冲写入器，支持"预留-回填"模式。它解决了序列化过程中需要先写入数据、再回填长度或校验码的常见问题。

### **核心特性**
1. **预留空间**：`ReserveSpan(count, out token)` - 预留指定大小的空间
2. **回填数据**：`Commit(token)` - 提交预留空间的数据
3. **高性能**：基于ArrayPool的分块内存管理
4. **透传优化**：无预留时直接使用底层writer，零开销

---

## 🚀 **典型使用场景**

### **场景1：消息分帧（最常见）**
```csharp
// 目标格式：Magic(4) | Length(4) | Data | CRC32(4)
using var writer = new ChunkedReservableWriter(innerWriter);

// 1. 写入Magic
writer.GetSpan(4)[..4] = "MEMO"u8;
writer.Advance(4);

// 2. 预留Length字段
var lengthSpan = writer.ReserveSpan(4, out int lengthToken, "length");

// 3. 写入实际数据
var dataStart = writer.WrittenLength;
WriteMessageData(writer); // 假设写入了N字节
var dataLength = writer.WrittenLength - dataStart;

// 4. 回填Length
BitConverter.GetBytes((uint)dataLength).CopyTo(lengthSpan);
writer.Commit(lengthToken);

// 5. 预留并回填CRC32
var crcSpan = writer.ReserveSpan(4, out int crcToken, "crc32");
var crc = CalculateCRC32(/* 已写入的数据 */);
BitConverter.GetBytes(crc).CopyTo(crcSpan);
writer.Commit(crcToken);
```

### **场景2：嵌套结构序列化**
```csharp
// JSON-like: {"items":[...], "count":N}
using var writer = new ChunkedReservableWriter(innerWriter);

writer.Write("{\"items\":["u8);

// 预留count字段，稍后回填
var countSpan = writer.ReserveSpan(10, out int countToken, "item-count");

int itemCount = 0;
foreach (var item in items) {
    if (itemCount > 0) writer.Write(","u8);
    SerializeItem(writer, item);
    itemCount++;
}

writer.Write("],\"count\":"u8);
// 回填实际的item数量
var countBytes = Encoding.UTF8.GetBytes(itemCount.ToString());
countBytes.CopyTo(countSpan[..countBytes.Length]);
writer.Commit(countToken);
writer.Write("}"u8);
```

---

## ⚙️ **配置选项**

### **ChunkedReservableWriterOptions**
```csharp
var options = new ChunkedReservableWriterOptions {
    MinChunkSize = 4096,        // 最小块大小
    MaxChunkSize = 65536,       // 最大块大小
    Pool = ArrayPool<byte>.Shared // 自定义内存池
};

using var writer = new ChunkedReservableWriter(innerWriter, options);
```

### **关键配置说明**
- **MinChunkSize/MaxChunkSize**: 控制内存块大小，影响内存使用和性能
- **Pool**: 可注入自定义ArrayPool，用于测试或特殊场景
- **严格调用约定**: `GetSpan`/`GetMemory` 必须与 `Advance` 严格配对；若需要放弃缓冲，可调用 `Advance(0)`。`ReserveSpan` 只能在没有未 `Advance` 的缓冲时调用，否则会抛出 `InvalidOperationException`。

---

## 📊 **性能特性**

### **内存管理**
- **ArrayPool优化**: 避免GC压力，重用内存块
- **自适应增长**: 块大小按2的幂次增长，优化ArrayPool bucket locality
- **智能压缩**: 当空闲空间过多时自动压缩，平衡内存使用

### **操作复杂度**
- **写入操作**: O(1) - GetSpan/Advance
- **预留操作**: O(1) - ReserveSpan
- **提交操作**: O(1) - Commit（可能触发O(n)的flush）
- **透传模式**: O(1) - 无预留时零开销

---

## 🔍 **诊断和调试**

### **状态监控属性**
```csharp
writer.WrittenLength        // 总写入长度
writer.FlushedLength        // 已flush到底层writer的长度
writer.PendingLength        // 待flush的长度
writer.PendingReservationCount // 未提交的预留数量
writer.BlockingReservationToken // 阻塞flush的第一个token
writer.IsPassthrough        // 是否处于透传模式
```

### **调试技巧**
1. **使用tag参数**: `ReserveSpan(count, out token, "my-field")` 便于定位问题
2. **检查BlockingReservationToken**: 找出哪个预留阻塞了数据flush
3. **监控PendingLength**: 确保数据及时flush到底层writer

---

## ⚠️ **使用注意事项**

### **必须遵守的规则**
1. **先 Advance 再下一步**: 每次 `GetSpan`/`GetMemory` 后必须调用 `Advance`（可用 `Advance(0)` 释放未使用的缓冲），否则下一次 `GetSpan` 或 `ReserveSpan` 将抛出异常。
2. **按顺序提交**: 预留必须按创建顺序提交，不能跳跃
3. **及时提交**: 长时间不提交会阻塞数据flush
4. **正确Dispose**: 使用using语句或手动调用Dispose释放资源

### **常见错误**
```csharp
// ❌ 错误：跳跃提交
var span1 = writer.ReserveSpan(4, out int token1);
var span2 = writer.ReserveSpan(4, out int token2);
writer.Commit(token2); // 错误！必须先提交token1

// ❌ 错误：重复提交
writer.Commit(token1);
writer.Commit(token1); // 错误！token已失效

// ✅ 正确：按顺序提交
writer.Commit(token1);
writer.Commit(token2);
```

---

## 🧪 **测试和验证**

### **单元测试覆盖**
- **P1Tests**: 基础功能测试（写入、预留、提交）
- **P2Tests**: 高级场景测试（多重预留、复杂序列化）
- **StatsTests**: 状态监控和诊断功能测试
- **NegativeTests**: 错误处理和边界情况测试
- **OptionsTests**: 配置选项验证测试

### **性能验证**
```csharp
// 基准测试示例
[Benchmark]
public void WriteWithReservation() {
    using var writer = new ChunkedReservableWriter(_innerWriter);
    var span = writer.ReserveSpan(4, out int token);
    writer.GetSpan(1000).Fill(42);
    writer.Advance(1000);
    BitConverter.GetBytes(1000).CopyTo(span);
    writer.Commit(token);
}
```

---

## 🎨 **设计哲学**

ChunkedReservableWriter体现了以下设计原则：
1. **优雅与实用的平衡**: 既要API简洁，又要功能强大
2. **性能与可读性并重**: 高性能实现不牺牲代码清晰度
3. **渐进式优化**: 先保证正确性，再优化性能细节
4. **用户体验导向**: 提供丰富的诊断信息和调试支持

---

## 🔗 **相关资源**

- **源码**: atelia-storage 中的 `src/Data/ChunkedReservableWriter.cs`（[按固定版本定位](storage-dependency.md)）
- **接口定义**: atelia-storage 中的 `src/Data/IReservableBufferWriter.cs`（[按固定版本定位](storage-dependency.md)）
- **配置选项**: 历史路径 `src/Data/ChunkedReservableWriterOptions.cs`，以[固定版本源码](storage-dependency.md)为准
- **测试用例**: 历史路径 `src/Data/*Tests.cs`；现行测试在 atelia-storage 的 `tests/Data.Tests/`（[按固定版本定位](storage-dependency.md)）
- **设计讨论**: `docs/MessageHistoryStorage/`

---

## 💡 **最佳实践总结**

1. **合理配置块大小**: 根据数据特点调整MinChunkSize/MaxChunkSize
2. **及时提交预留**: 避免长时间持有未提交的预留空间
3. **使用调试标记**: 为预留空间添加有意义的tag便于调试
4. **监控性能指标**: 定期检查PendingLength和flush效率
5. **正确资源管理**: 始终使用using语句确保资源释放

**ChunkedReservableWriter - 让复杂的序列化变得简单而高效！** 🚀

---
*文档作者: 刘德智*
*最后更新: 2025-08-26 08:00*
