# Nexus Agent 程序集与命名空间规划

## 总体架构

基于 `endless-session-road-map.md` 的需求分析，我们将在 Atelia.sln 中实现完整的 Nexus Agent 系统。

## 程序集划分

### 存储层程序集

#### 1. Atelia.IO.BinaryLog
**路径**: `src/IO.BinaryLog/`
**职责**: 第0层 - 双向遍历与二进制分帧
```csharp
namespace Atelia.IO.BinaryLog
{
    // 核心类型
    public sealed class BinaryLogWriter : IDisposable
    public sealed class BinaryLogCursor : IDisposable
    public ref struct EnvelopeScope : IBufferWriter<byte>, IDisposable
    public static class BinaryLogFormat
    public static class BinaryLogEnumerable
}
```

#### 2. Atelia.IO.LogStorage
**路径**: `src/IO.LogStorage/`
**职责**: 第1层 - 通用日志存储抽象
```csharp
namespace Atelia.IO.LogStorage
{
    // 核心接口
    public interface ILogStorage : IDisposable
    public interface ILogStorageProvider

    // 实现类
    public class BinaryLogStorage : ILogStorage
    public class LogStorageOptions
    public class LogPosition
    public class LogReadOptions

    // 文件管理
    public class StoragePartitionManager
    public class MirrorSyncService
}
```

#### 3. Atelia.Storage.MessageHistory
**路径**: `src/Storage.MessageHistory/`
**职责**: 第2层 - Agent消息历史专用存储
```csharp
namespace Atelia.Storage.MessageHistory
{
    // 消息类型
    public class LlmMessage
    public class ConversationSession

    // 存储服务
    public class MessageHistoryStorage : IDisposable
    public class MessageHistoryOptions

    // 压缩与管理
    public interface IContextCompressor
    public class SmartContextCompressor : IContextCompressor
}
```

### Agent核心程序集

#### 4. Nexus.Agent.Core
**路径**: `src/Nexus.Agent.Core/`
**职责**: Agent核心逻辑与抽象接口
```csharp
namespace Nexus.Agent.Core
{
    // 主Agent类
    public class BasicAgent : IDisposable
    public class NexusAgent : IDisposable  // 未来的高级版本

    // LLM适配层
    public interface ILlmAdapter
    public interface ILlmContextComposer
    public interface IUserMessageComposer

    // 上下文管理
    public class LlmContext
    public class BasicLlmContextComposer : ILlmContextComposer
    public class BasicUserMessageComposer : IUserMessageComposer
}
```

#### 5. Nexus.Agent.LLM
**路径**: `src/Nexus.Agent.LLM/`
**职责**: LLM服务提供者实现
```csharp
namespace Nexus.Agent.LLM
{
    // Copilot Chat集成
    public class ClaudeOverCopilotChat : ILlmAdapter
    public class CopilotChatClient

    // 其他LLM提供者
    public class OpenAIAdapter : ILlmAdapter
    public class GeminiAdapter : ILlmAdapter

    // 多模型聚合
    public class MultiModelLlmAdapter : ILlmAdapter
    public class LlmResourcePool
}
```

#### 6. Nexus.Agent.Tools
**路径**: `src/Nexus.Agent.Tools/`
**职责**: 工具调用系统
```csharp
namespace Nexus.Agent.Tools
{
    // 工具抽象
    public interface IFunction
    public interface ICallingStrategy

    // 调用策略
    public class ToolCallingStrategy : ICallingStrategy
    public class ContentStreamCallingStrategy : ICallingStrategy  // 占位

    // 基础工具
    public class SayFunction : IFunction
    public class ThinkFunction : IFunction
    public class FileSystemTools : IFunction

    // 工具管理
    public class FunctionRegistry
    public class ToolCallParser
}
```

## 依赖关系图

```
Nexus.Agent.Core
├── depends on → Atelia.Storage.MessageHistory
├── depends on → Nexus.Agent.LLM
└── depends on → Nexus.Agent.Tools

Atelia.Storage.MessageHistory
└── depends on → Atelia.IO.LogStorage

Atelia.IO.LogStorage
└── depends on → Atelia.IO.BinaryLog

Nexus.Agent.LLM
└── depends on → Nexus.Agent.Core (interfaces only)

Nexus.Agent.Tools
└── depends on → Nexus.Agent.Core (interfaces only)
```

## 阶段1实现优先级

### 立即实现 (本周)
1. **Atelia.IO.BinaryLog** - 基础二进制分帧
2. **Atelia.IO.LogStorage** - 基础日志存储
3. **Nexus.Agent.Core** - 核心接口定义

### 第二优先级 (下周)
4. **Atelia.Storage.MessageHistory** - 消息历史存储
5. **Nexus.Agent.LLM** - Copilot Chat适配器
6. **Nexus.Agent.Tools** - 基础工具实现

### 集成测试 (第三周)
7. **BasicAgent** 完整实现
8. **端到端测试** - 简单对话循环
9. **持续运行测试** - 永续会话验证

## 项目文件结构

```
src/
├── Atelia.IO.BinaryLog/
│   ├── Atelia.IO.BinaryLog.csproj
│   ├── BinaryLogWriter.cs
│   ├── BinaryLogCursor.cs
│   └── BinaryLogFormat.cs
├── Atelia.IO.LogStorage/
│   ├── Atelia.IO.LogStorage.csproj
│   ├── ILogStorage.cs
│   ├── BinaryLogStorage.cs
│   └── LogStorageOptions.cs
├── Atelia.Storage.MessageHistory/
│   ├── Atelia.Storage.MessageHistory.csproj
│   ├── MessageHistoryStorage.cs
│   ├── LlmMessage.cs
│   └── SmartContextCompressor.cs
├── Nexus.Agent.Core/
│   ├── Nexus.Agent.Core.csproj
│   ├── BasicAgent.cs
│   ├── ILlmAdapter.cs
│   └── LlmContext.cs
├── Nexus.Agent.LLM/
│   ├── Nexus.Agent.LLM.csproj
│   ├── ClaudeOverCopilotChat.cs
│   └── CopilotChatClient.cs
└── Nexus.Agent.Tools/
    ├── Nexus.Agent.Tools.csproj
    ├── IFunction.cs
    ├── SayFunction.cs
    └── ToolCallingStrategy.cs
```

## 测试项目结构

```
tests/
├── Atelia.IO.BinaryLog.Tests/
├── Atelia.IO.LogStorage.Tests/
├── Atelia.Storage.MessageHistory.Tests/
├── Nexus.Agent.Core.Tests/
├── Nexus.Agent.LLM.Tests/
├── Nexus.Agent.Tools.Tests/
└── Nexus.Agent.Integration.Tests/  # 端到端测试
```

## 配置与部署

### appsettings.json 结构
```json
{
  "Nexus": {
    "Storage": {
      "MessageHistory": {
        "PrimaryPath": "./data/messages",
        "MirrorPath": "./backup/messages",
        "MaxFileSize": "100MB",
        "CompressionThreshold": 8000
      }
    },
    "LLM": {
      "DefaultProvider": "CopilotChat",
      "CopilotChat": {
        "BaseUrl": "http://localhost:4000",
        "DefaultModel": "claude-sonnet-4"
      }
    },
    "Agent": {
      "SystemInstruction": "./config/system-instruction.md",
      "ThinkingInterval": "00:00:30",
      "MaxContextLength": 32000
    }
  }
}
```

---

*文档版本：v1.0*
*创建时间：2025-08-23*
*基于：endless-session-road-map.md*
*维护者：刘德智*
