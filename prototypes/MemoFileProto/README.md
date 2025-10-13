dotnet run
# MemoFileProto

MemoFileProto 是一个实验性的多轮对话原型，展示了如何通过统一的 `Universal*` 数据模型，同时驱动不同 Provider（OpenAI、Anthropic）的 Chat 接口，并让 LLM 主动物理化“记忆”与工具调用。

## 功能亮点

- ✅ **多轮对话**：保留完整历史，支持系统提示词即时切换
- ✅ **流式输出**：实时渲染增量响应
- ✅ **结构化上下文**：自动注入时间戳、记忆、环境信息
- ✅ **长期记忆本**：LLM 可使用工具精细编辑记忆 Notebook
- ✅ **Tool Calling 框架**：Provider 无关，统一注册/执行
- ✅ **多 Provider 支持**：`OpenAIClient`、`AnthropicClient` 均实现 `ILLMClient`

## 运行环境

- **框架**：.NET 9.0
- **默认端点**：`http://localhost:4000`
  - OpenAI 兼容接口：`/openai/v1/chat/completions`
  - Anthropic 兼容接口：`/anthropic/v1/messages`
- **默认模型**：`vscode-lm-proxy`
- **输出模式**：SSE 流

## 项目总览

```
MemoFileProto/
├── Agent/
│   └── LlmAgent.cs                # 面向应用的高层 Agent，使用 Universal* 模型
├── Models/
│   ├── UniversalMessage.cs        # Provider 无关的消息 / 工具调用 / 结果
│   ├── UniversalRequest.cs        # Provider 无关的请求模型
│   ├── UniversalResponseDelta.cs  # Provider 无关的流式增量
│   ├── UniversalTool.cs           # 通用工具定义
│   ├── ToolCallAccumulator.cs     # 流式工具调用组装器
│   ├── OpenAI/…                   # OpenAI 专用 DTO
│   └── Anthropic/…                # Anthropic 专用 DTO（含流式事件）
├── Services/
│   ├── ILLMClient.cs              # Provider 无关接口
│   ├── OpenAIClient.cs            # Universal ↔ OpenAI 转换 + 流解析
│   └── AnthropicClient.cs         # Universal ↔ Anthropic 转换 + 工具聚合
├── Tools/
│   ├── ITool.cs                   # 工具接口（返回 UniversalTool）
│   ├── MemoReplaceLiteralTool.cs  # 精确替换/追加记忆
│   ├── MemoReplaceSpanTool.cs     # 区域替换记忆
│   ├── StringReplaceTool.cs       # 示例工具
│   └── ToolManager.cs             # 工具注册/执行调度
├── README_LLM_Architecture.md     # 架构长文档（扩展阅读）
└── Program.cs                     # 控制台交互入口
```

> 若要深入了解 Universal → Provider 转换，以及工具聚合策略，可参阅 `README_LLM_Architecture.md`。

## 快速开始

```powershell
cd prototypes/MemoFileProto
dotnet run
```

首次启动会看到控制台命令提示；常用命令如下：

- `/system <提示词>`：设置系统提示词（会重置历史）
- `/notebook`：查看当前 Memory Notebook
- `/history`：查看结构化对话历史
- `/clear`：清空对话
- `/exit` 或 `/quit`：退出

## 与 LLM 的交互流程

1. **消息入栈**：`LlmAgent` 将用户输入打包为结构化 `UniversalMessage`
2. **上下文构建**：动态拼接历史、记忆、环境区块
3. **协议转换**：交由 `ILLMClient` 实现（OpenAI / Anthropic）完成格式映射
4. **流式消费**：`UniversalResponseDelta` 带回文本增量和工具调用片段
5. **工具执行**：调用 `ToolManager.ExecuteToolAsync`，并将结果写回历史

> 工具结果在 OpenAI 协议里是多条 `role=tool` 消息；在 Anthropic 协议里会被聚合为 `type=tool_result` 内容块。Universal 层屏蔽了这些差异。

## 记忆工具备忘

| 工具 | 描述 | 特点 |
| ---- | ---- | ---- |
| `memory_notebook_replace` | 字面匹配替换/追加 | 支持锚点 `search_after`、空字符串追加 |
| `memory_notebook_replace_span` | 区域替换 | 需要起止标记，适合块状编辑 |
| `string_replace` | 演示用工具 | 简单字符串替换示例 |

所有工具均实现 `ITool`，并返回 `UniversalTool` 定义，因此能够在不同 Provider 间复用。

## 下一步想法

- ✅ 引入 Anthropic 协议支持
- ✅ 统一 Universal 数据模型
- ☐ 对话/记忆持久化存储
- ☐ 更丰富的环境感知（工作区概览、文件片段等）
- ☐ 对话窗口截断策略与记忆整理助手

## 依赖

- `System.Text.Json` 9.0.0

## 注意事项

1. 默认仅启用 OpenAI 客户端；若要使用 Anthropic 兼容端点，可在 `Program.cs` 中切换 `ILLMClient` 实例。
2. 该项目仍处于快速迭代阶段，协议及工具接口随时可能调整。
