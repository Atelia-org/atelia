# MemoFileProto

一个简易的多轮 LLM 对话原型程序，用于测试和演示与本地 OpenAI 兼容端点的交互。

## 功能特性

- ✅ 多轮对话支持（带历史记录）
- ✅ 流式输出（实时显示 LLM 响应）
- ✅ 系统提示词支持
- ✅ 工具系统框架（已预留 `string_replace` 工具扩展点）
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
│   ├── ChatMessage.cs      # 消息模型（支持工具调用）
│   ├── ChatRequest.cs       # 请求模型
│   └── ChatResponse.cs      # 响应模型
├── Services/
│   └── OpenAIClient.cs      # OpenAI 兼容客户端（流式输出）
├── Tools/
│   ├── ITool.cs             # 工具接口
│   ├── StringReplaceTool.cs # string_replace 工具实现示例
│   └── ToolManager.cs       # 工具管理器
└── Program.cs               # 主程序入口
```

## 使用方法

### 运行程序

```bash
cd prototypes/MemoFileProto
dotnet run
```

### 命令列表

- `/system <提示词>` - 设置或修改系统提示词
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

### 1. 消息模型（Models）

- 完整支持 OpenAI Chat Completion API 格式
- 支持工具调用（tool_calls）和工具响应
- 使用 `System.Text.Json` 进行序列化

### 2. OpenAI 客户端（Services）

- **流式输出**: 使用 `IAsyncEnumerable<string>` 实现增量响应
- **SSE 解析**: 正确处理 `data:` 前缀和 `[DONE]` 标记
- **错误处理**: 跳过无法解析的 JSON 行（容错机制）

### 3. 工具系统（Tools）

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

1. **注册新工具**: 在 `ToolManager` 构造函数中调用 `RegisterTool()`
2. **工具定义**: 自动生成符合 OpenAI 规范的 JSON Schema
3. **工具执行**: 异步执行并返回结果

#### 示例：添加新工具

```csharp
// 1. 实现 ITool 接口
public class FileReadTool : ITool
{
    public string Name => "file_read";
    public string Description => "读取文件内容";

    public Tool GetToolDefinition()
    {
        return new Tool
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = Name,
                Description = Description,
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string", description = "文件路径" }
                    },
                    required = new[] { "path" }
                }
            }
        };
    }

    public async Task<string> ExecuteAsync(string arguments)
    {
        // 实现逻辑
    }
}

// 2. 在 ToolManager 中注册
RegisterTool(new FileReadTool());
```

### 4. 对话管理

- **历史记录**: 在内存中维护完整对话上下文
- **系统提示词**: 动态设置并自动重置历史
- **无持久化**: 当前版本不保存到磁盘（按需求设计）

## 下一步扩展方向

1. **完整工具调用**: 实现 LLM 主动调用工具的完整流程
2. **文件操作工具**: 添加 `file_read`、`file_write` 等工具
3. **对话持久化**: 保存和加载对话历史到文件
4. **配置文件**: 支持从配置文件读取端点、模型等参数
5. **错误重试**: 添加网络请求重试机制

## 依赖项

- `System.Text.Json` (9.0.0) - JSON 序列化

## 注意事项

1. 确保本地 OpenAI 兼容服务已启动在 `http://localhost:4000`
2. 如果端点地址不同，可以修改 `Program.cs` 中的 `OpenAIClient` 初始化参数
3. 工具调用功能已预留，但当前版本 LLM 不会主动触发（需要完善工具调用流程）
