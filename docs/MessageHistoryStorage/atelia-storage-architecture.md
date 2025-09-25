# Atelia 存储系统架构规划

## 概述

为实现永续会话Agent环境，我们设计了分层的存储系统架构，支持消息历史、认知节点等多种数据的高效存储与检索。

## 架构分层

### 第0层：Atelia.IO.BinaryLog
**职责**：纯二进制分帧，双向遍历
- `BinaryLogWriter` - 高效写入，支持流式和一次性写入
- `BinaryLogCursor` - 双向遍历，支持从头/从尾开始
- `BinaryLogFormat` - 统一的二进制格式定义

**特性**：
- Magic开头 + 双端长度记录 + CRC32C校验
- 4字节对齐优化
- 无文件系统依赖，可用于内存、网络流

**格式**：`Magic(4) | EnvLen(4) | Envelope | Padding(0~3) | EnvLen(4) | CRC32C(4)`

### 第1层：Atelia.IO.LogStorage
**职责**：通用日志存储抽象
- `ILogStorage` - 统一的日志存储接口
- `BinaryLogStorage` - 基于BinaryLog的实现
- 文件分段、按月分文件夹
- 双写逻辑（主存储 + 镜像存储）
- 数据检查与恢复工具

**核心接口**：
```csharp
public interface ILogStorage : IDisposable
{
    Task AppendAsync<T>(T item, CancellationToken ct = default);
    IAsyncEnumerable<T> ReadAsync<T>(LogReadOptions? options = null);
    Task<LogPosition> GetCurrentPositionAsync();
    IAsyncEnumerable<T> ReadFromAsync<T>(LogPosition position);
    Task CompactAsync(TimeSpan olderThan);
    Task VerifyIntegrityAsync();
    Task RepairAsync();
}
```

### 第2层：领域专用存储

#### Atelia.Storage.MessageHistory
**职责**：Agent消息历史存储
- 基于 `ILogStorage` 实现
- Message 特定的序列化优化
- 会话管理和上下文压缩
- 支持按时间范围、会话ID检索

#### Atelia.Storage.MemoTree
**职责**：认知节点存储（未来）
- 认知节点的版本化存储
- 关系图的持久化
- 支持LOD（Level of Detail）机制

## 技术特性

### 双写机制
```csharp
public class LogStorageOptions
{
    public string PrimaryPath { get; set; }
    public string? MirrorPath { get; set; }  // null = 单写模式
    public bool VerifyMirrorOnRead { get; set; } = true;
    public bool AutoRepairInconsistency { get; set; } = true;
}
```

### 文件组织结构
```
/storage-root/
├── 2025-08/           # 按月分文件夹
│   ├── messages.001   # 按容量分段
│   ├── messages.002
│   └── messages.idx   # 索引文件
├── 2025-09/
└── .metadata/         # 元数据和配置
```

### Envelope内部Header
轻量级header标记内容格式：
1. **UTF-8 String** - 纯文本消息
2. **UTF-8 JSON** - 结构化数据
3. **Self-Describing Binary** - 自描述二进制
4. **Meta+Data Split** - 元数据与数据分离（未来）
5. **Custom Binary Format** - 自制序列化格式（未来）

## 演进路径

### 当前阶段（MVP）
- 实现 Atelia.IO.BinaryLog 核心功能
- 实现 Atelia.IO.LogStorage 基础抽象
- 实现 Atelia.Storage.MessageHistory 基本功能

### 中期目标
- 完善双写和恢复机制
- 优化文件分段和索引策略
- 集成到Nexus Agent环境

### 长期愿景
- 支持分布式存储
- 实现自制高效序列化格式
- 与MemoTree深度集成

## 复用价值

这套架构不仅服务于Agent消息历史，还可以支持：
- 系统审计日志
- 事件溯源存储
- 实时数据流处理
- 任何需要高效追加写入的场景

## 技术债务管理

### 已知限制
- 当前仅支持追加写入，不支持随机更新
- 文件分段策略相对简单
- 索引机制待完善

### 未来优化
- 考虑引入LSM-Tree结构
- 支持压缩和去重
- 实现更智能的缓存策略

---

*文档版本：v1.0*
*创建时间：2025-08-23*
*维护者：刘德智*
