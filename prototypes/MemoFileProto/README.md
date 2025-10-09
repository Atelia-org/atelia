# MemoFileProto

一个简易的多轮 LLM 对话原型程序，用于测试和演示与本地 OpenAI 兼容端点的交互。

## 功能特性

- ✅ 多轮对话支持（带历史记录）
- ✅ 流式输出（实时显示 LLM 响应）
- ✅ 系统提示词支持
- ✅ **结构化感官数据包**（时间戳、记忆注入）
- ✅ **长期记忆系统**（LLM 可自主编辑记忆文档）
- ✅ **工具系统框架**（已实现 `memory_notebook_replace ` 工具）
- ✅ 简单的命令行交互界面

## 技术规格

- **框架**: .NET 9.0
- **端点**: `http://localhost:4000/openai/v1/chat/completions`
- **模型**: `gpt-4.1`
- **认证**: 无需 API Key
- **输出模式**: 流式（Server-Sent Events）

## 项目结构

```
MemoFileProto/
├── Models/
│   ├── ChatMessage.cs          # 消息模型（支持工具调用 + Timestamp 元数据）
│   ├── ChatRequest.cs           # 请求模型
│   ├── ChatResponse.cs          # 响应模型
│   ├── ChatResponseDelta.cs     # 流式响应增量
│   └── ToolCallAccumulator.cs   # 工具调用累加器
├── Services/
│   └── OpenAIClient.cs          # OpenAI 兼容客户端（流式输出）
├── Tools/
│   ├── ITool.cs                 # 工具接口
│   ├── EditMyMemoryTool.cs      # 编辑记忆文档工具（string replace）
│   └── ToolManager.cs           # 工具管理器
└── Program.cs                   # 主程序入口
```

## 使用方法

### 运行程序

```bash
cd prototypes/MemoFileProto
dotnet run
```

### 命令列表

- `/system <提示词>` - 设置或修改系统提示词
- `/memory` - 查看当前记忆文档（当前仅支持查看）
- `/clear` - 清空对话历史
- `/history` - 查看完整对话历史
- `/exit` 或 `/quit` - 退出程序

### 使用示例

```
User> 你好，请介绍一下自己
Assistant> （流式输出响应）

User> /system 你是一个专业的技术文档写作助手
系统提示词已设置为: 你是一个专业的技术文档写作助手

User> 帮我写一段关于 REST API 的介绍
Assistant> （根据新的系统提示词回答）

User> /history
=== 对话历史 ===
[0] system: 你是一个专业的技术文档写作助手
[1] user: 帮我写一段关于 REST API 的介绍
[2] assistant: ...
===============

User> /exit
再见！
```

## 架构设计

### 1. 长期记忆系统（核心特性）

#### **概念分离**
- **`_conversationHistory`**: 纯粹的对话历史，只追加，不含注入内容
- **`_memoryFile`**: 长期记忆文档，类似日记/回忆录，用第一人称记录
- **`_systemInstruction`**: 系统指令，引导 LLM 理解消息格式

#### **感官数据包（Sensory Data Package）**
每条用户消息都结构化为：
```markdown
## 当前时间
2025-10-07 14:30:15

## 收到用户发来的消息
你好，请介绍一下自己
```

#### **动态上下文构建**
调用 LLM 时，`BuildContext()` 方法：
1. 添加 system instruction
2. 遍历历史消息
3. **只在最后一条 user message 注入 MemoryFile**

LLM 实际看到的消息：
```markdown
## 你的记忆
我喜欢简洁的代码风格。
我正在学习 C# 和 .NET。

## 当前时间
2025-10-07 14:30:15

## 收到用户发来的消息
你好，请介绍一下自己
```

#### **记忆编辑工具**
- **工具名**: `memory_notebook_replace `
- **编辑方式**: String Replace（简单但有效）
- **参数**:
  - `old_text`: 要替换的旧文本（精确匹配）
  - `new_text`: 替换后的新文本（可为空表示删除）
- **特殊用法**: `old_text` 为空时，追加 `new_text` 到记忆末尾

### 2. 消息模型（Models）

- 完整支持 OpenAI Chat Completion API 格式
- 支持工具调用（tool_calls）和工具响应
- **新增 `Timestamp` 元数据**：记录消息创建时间（不发送给 LLM）
- 使用 `System.Text.Json` 进行序列化

### 3. OpenAI 客户端（Services）

- **流式输出**: 使用 `IAsyncEnumerable<string>` 实现增量响应
- **SSE 解析**: 正确处理 `data:` 前缀和 `[DONE]` 标记
- **错误处理**: 跳过无法解析的 JSON 行（容错机制）

### 4. 工具系统（Tools）

#### 接口设计

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    Tool GetToolDefinition();
    Task<string> ExecuteAsync(string arguments);
}
```

#### 扩展点

工具系统已设计为可扩展框架，支持：

1. **注册新工具**: 通过依赖注入传递必要的上下文（如 `_memoryFile` 访问）
2. **工具定义**: 自动生成符合 OpenAI 规范的 JSON Schema
3. **工具执行**: 异步执行并返回结果

#### 示例：EditMyMemoryTool 实现

```csharp
// 1. 实现 ITool 接口，通过构造函数接收依赖
public class EditMyMemoryTool : ITool
{
    private readonly Func<string> _getMemory;
    private readonly Action<string> _setMemory;

    public EditMyMemoryTool(Func<string> getMemory, Action<string> setMemory)
    {
        _getMemory = getMemory;
        _setMemory = setMemory;
    }

    // ... 实现接口方法
}

// 2. 在 Program.cs 中注册（使用 Lazy 延迟初始化）
private static readonly Lazy<ToolManager> _toolManagerLazy = new(() => {
    var manager = new ToolManager();
    manager.RegisterTool(new EditMyMemoryTool(
        getMemory: () => _memoryFile,
        setMemory: (newMemory) => _memoryFile = newMemory
    ));
    return manager;
});
```

### 5. 对话管理

- **历史记录**: 在内存中维护完整对话上下文（结构化格式）
- **系统提示词**: 动态设置并自动重置历史
- **记忆注入**: 只在最后一条 user message 注入，避免重复
- **无持久化**: 当前版本不保存到磁盘（按实验需求设计）

## 下一步扩展方向

1. ~~**实现 `EditMyMemory` 工具**~~ ✅ 已完成
2. ~~**添加 `/memory` 命令**~~ ✅ 已完成
3. **测试 LLM 记忆编辑能力**：验证 LLM 是否能自主选择遗忘和记忆
4. **添加更多感官数据**：工作目录、文件列表、环境变量等
5. **实现对话历史截断策略**：防止上下文溢出
6. **对话持久化**：保存和加载 history 和 memory 到文件
7. **记忆文档版本控制**：回滚机制，防止误删
8. **Info Provider 抽象**：可配置的信息注入器（时间、环境、文件等）

## 依赖项

- `System.Text.Json` (9.0.0) - JSON 序列化

## 注意事项

1. 确保本地 OpenAI 兼容服务已启动在 `http://localhost:4000`
2. 如果端点地址不同，可以修改 `Program.cs` 中的 `OpenAIClient` 初始化参数
3. 工具调用功能已预留，但当前版本 LLM 不会主动触发（需要完善工具调用流程）
