# NexusZero

一个最小可用的 .NET 9 控制台应用，用于通过 OpenRouter 调用多种 LLM 模型（模型 ID 与 `example/ref_code/example_openrouter.py` 保持一致）。

## 先决条件
- .NET SDK 9.0+
- OpenRouter API Key（环境变量：`OPENROUTER_API_KEY`）

## 快速开始（PowerShell）

```powershell
# 进入项目目录
Set-Location -LiteralPath E:\repos\Atelia-org\MemoTree\prototypes\NexusZero

# 设置 API Key（当前会话）
$Env:OPENROUTER_API_KEY = 'sk-xxxxxxxxxxxxxxxxxxxxxxxx'

# 可选：切换模型（默认 anthropic/claude-sonnet-4）
# $Env:OPENROUTER_MODEL = 'qwen/qwen3-32b'

# 构建与运行
dotnet build
 dotnet run
```

## 模型常量
- anthropic/claude-sonnet-4（默认）
- google/gemini-2.5-pro
- openrouter/horizon-beta
- qwen/qwen3-32b
- qwen/qwen3-30b-a3b-instruct-2507
- z-ai/glm-4.5-air:free
- z-ai/glm-4.5
- qwen/qwen3-coder:free
- deepseek/deepseek-r1-0528:free
- deepseek/deepseek-chat-v3-0324:free

## 运行效果
- 若未设置 `OPENROUTER_API_KEY`，程序会给出友好提示并退出。
- 成功时会打印一次 Assistant 的回复内容。

## 备注
- 你可以在 `Program.cs` 中扩展参数（如 temperature、max_tokens、reasoning 等）。
- 推荐在请求头中添加 `HTTP-Referer` 与 `X-Title` 标识你的应用来源（目前示例中已留下注释）。
