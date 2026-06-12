# FamilyChat.Server

家庭局域网里的单会话 Chat 服务。每个账号绑定一个固定 `ChatSession`，没有“新建会话”与历史翻页，只显示最近 `6` 个可见轮次。

## 快速开始

启动命令：

```bash
dotnet run --project prototypes/FamilyChat.Server
```

第一次启动时，如果 `.atelia/family-chat/config.json` 不存在，程序会自动生成一份模板，然后退出并提示你先修改配置。

默认模板按“本机 `sglang` / OpenAI-compatible 服务监听 `http://localhost:8888/`”来写，Web 服务默认监听：

```text
http://0.0.0.0:3510
```

局域网内其他设备可通过 `http://<你的局域网IP>:3510` 访问。

## 最小改法

通常只需要改这几项就能开始使用：

1. 把两个用户的 `modelId` 改成你本地真实加载的模型名。
2. 把模板里的弱口令 `alice123` / `bob123` 改掉。
3. 如果你不想监听全部网卡，就把 `listenUrls` 改成你想绑定的地址。

`modelId` 如果保留模板占位值，服务仍然能启动，但真正发送消息时会由底层 LLM backend 报“模型不存在”之类的错误。

## 默认配置文件

默认路径：

```text
.atelia/family-chat/config.json
```

自动生成的模板示例：

```json
{
  "backend": {
    "kind": "openai-chat",
    "baseAddress": "http://localhost:8888/",
    "apiKey": null
  },
  "users": [
    {
      "userId": "alice",
      "displayName": "Alice",
      "password": "alice123",
      "sessionDir": ".atelia/family-chat/sessions/alice",
      "modelId": "REPLACE_WITH_YOUR_LOCAL_MODEL_ID",
      "completionSurfaceId": "openai-chat/sglang-compatible",
      "systemPrompt": "你是家庭局域网里的私人助手。优先用简洁、直接、可信的中文回答。不确定时明确说明不确定，不编造细节。",
      "compactionThresholdTokens": 32000,
      "compactionSystemPrompt": "你负责压缩长期对话上下文。请保留用户偏好、未完成事项、关键事实、约定、限制与后续行动线索。输出简洁中文摘要，避免虚构。",
      "compactionPrompt": "请把以上较早的对话压缩成一段可供后续继续聊天的中文 recap。保留人物偏好、进行中的任务、重要事实、未决问题与明确约定。"
    },
    {
      "userId": "bob",
      "displayName": "Bob",
      "password": "bob123",
      "sessionDir": ".atelia/family-chat/sessions/bob",
      "modelId": "REPLACE_WITH_YOUR_LOCAL_MODEL_ID",
      "completionSurfaceId": "openai-chat/sglang-compatible",
      "systemPrompt": "你是家庭局域网里的私人助手。优先用简洁、直接、可信的中文回答。不确定时明确说明不确定，不编造细节。",
      "compactionThresholdTokens": 32000,
      "compactionSystemPrompt": "你负责压缩长期对话上下文。请保留用户偏好、未完成事项、关键事实、约定、限制与后续行动线索。输出简洁中文摘要，避免虚构。",
      "compactionPrompt": "请把以上较早的对话压缩成一段可供后续继续聊天的中文 recap。保留人物偏好、进行中的任务、重要事实、未决问题与明确约定。"
    }
  ],
  "listenUrls": [
    "http://0.0.0.0:3510"
  ]
}
```

## 用 Markdown 文件撰写系统提示词

系统提示词通常很长，写在 `config.json` 的 `systemPrompt` 字符串里需要转义换行、难以编辑。为此每个用户支持一个 `systemPromptFile` 字段，指向一个 Markdown（或纯文本）文件，其内容会覆盖内联的 `systemPrompt`：

```json
{
  "userId": "alice",
  "systemPromptFile": "prompts/alice.md"
}
```

规则：

- `systemPromptFile` 为相对路径时，相对于 `config.json` 所在目录解析（绝对路径也可）。
- 文件内容加载后会 `Trim()` 首尾空白，整体作为该用户的系统提示词。
- 设置了 `systemPromptFile` 时无需再写 `systemPrompt`；若两者都给，文件内容优先。
- 文件不存在会在启动时报错并退出；最终系统提示词为空也会报错。

## 使用约束

- 每个账号只有一个固定会话，不提供新建会话。
- 页面只显示最近 `6` 个可见轮次，不支持回看更久历史。
- 当前交互只保留“发送”和“撤销上一轮”。点击“撤销上一轮”会直接取出最近一整轮，并把该轮 user message 回填到输入框；你可原样重发，也可修改后再发。
- 同一账号任一时刻只允许一个正在生成的 turn；并发发送会返回 `409`。
- 这是家庭局域网 MVP，不带 HTTPS、注册、改密、密码找回、后台管理。

## 常见坑

- 本地 LLM backend 没启动：页面发送后会报连接失败。
- `modelId` 不对：登录正常，但发消息时报模型不存在。
- 弱口令没改：只适合首次试跑，局域网长期使用前请尽快改掉。
