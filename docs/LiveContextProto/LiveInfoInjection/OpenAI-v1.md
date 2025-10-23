# OpenAI v1

## Role="tool" 消息结构

### 基本字段
- `role`: 固定字符串 `"tool"`，告诉服务端这是工具返回结果。
- `tool_call_id`: 必填，必须与上一条 `assistant` 消息中某个 `tool_calls[i].id` 完全一致，模型靠它把结果重新对号入座。
- `content`: 工具输出的主体。当前 v1 规范（兼容新版和旧版 Chat Completions）允许两种写法：
  1. **纯字符串**（向后兼容）
     ```json
     {
       "role": "tool",
       "tool_call_id": "call_abc123",
       "content": "{\"result\":\"...\"}"
     }
     ```
  2. **多片段数组**（推荐，覆盖文本/多模态/结构化数据）
     每个片段是一个对象，常用的 `type` 有：
     - `"text"`：纯文本。
     - `"input_json"`：结构化 JSON，适合把工具输出或上下文打包成对象。
     - `"input_audio"`、`"input_image"` 等多模态类型（若你的工具能返回此类内容）。

     示例：
     ```json
     {
       "role": "tool",
       "tool_call_id": "call_abc123",
       "content": [
         { "type": "input_json", "json": { "status": "ok", "payload": {...} } },
         { "type": "text", "text": "可读性更强的说明" }
       ]
     }
     ```
- 其他字段：当前规范不接受额外顶层字段（比如自定义的 `name`、`metadata`）；如需额外信息，请嵌入到 `content` 里。

### ToolCallResult 概念
- 在 OpenAI 文档与 SDK 中，`ToolCallResult` 通常指“开发者提交的工具执行结果”。它没有单独的 schema，而是通过上述 `role="tool"` 消息承载。
- 流式交互时，`assistant` 会推送一个含 `tool_calls` 的消息片段，你收到后必须补上对应的 `tool` 消息，服务端才会继续生成后续内容。

## 注入 Notification 信息的做法

### 设计建议
1. **JSON 包装结构化信息**
   - 使用 `content` 数组 + `"input_json"`，把 Notification 放在一个统一的 envelope 中，避免和纯文本混淆。
   - 推荐字段：
     - `tool_result`: 工具主输出。
     - `live_events`: 数组，列出你希望模型感知的事件。
     - `telemetry`: 诸如 token 预算、限流信息等统计数据。
     - `timestamp`: ISO 8601 时间戳，保留时区。

   例子：
   ```json
   {
     "role": "tool",
     "tool_call_id": "call_envSync",
     "content": [
       {
         "type": "input_json",
         "json": {
           "tool_result": {
             "status": "ok",
             "data": { /* 实际工具输出 */ }
           },
           "live_events": [
             {
               "type": "environment.clock",
               "timestamp": "2025-10-21T08:10:45Z",
               "payload": { "local_iso": "2025-10-21T16:10:45+08:00" }
             },
             {
               "type": "environment.token_budget",
               "timestamp": "2025-10-21T08:10:45Z",
               "payload": { "remaining": 12650, "unit": "tokens" }
             }
           ],
           "telemetry": {
             "latency_ms": 84,
             "invocation_id": "envSync-20251021-0810"
           }
         }
       },
       {
         "type": "text",
         "text": "环境同步完成，已返回最新时间与 Token 预算。"
       }
     ]
   }
   ```

2. **保持稳定的事件 schema**
   - 使用固定字段（`type` / `timestamp` / `payload`）帮助模型长期记住模式，减少解析负担。
   - 若 Notification 信息体量大，可只传增量（diff），并在 `telemetry` 中附带版本号。

3. **频率控制**
   - 模型对冗余信息敏感，建议只在每次工具调用返回时注入与该回合相关的事件。
   - 对于高频变量（如计时器），可以设置阈值（例如相隔 >30 秒或预算减少 >500 tokens 才播报一次）。

4. **可读 fallback**
   - 如果你担心模型忽略 JSON，可在 `content` 中追加一段简洁的文字总结（如上例中的第二个 `text` 片段）。

## 集成注意事项

- **对齐工具返回顺序**：如果一次 `assistant` 消息里有多个 `tool_calls`，必须为每个 id 分别提交 `role="tool"` 消息；顺序最好与调用顺序一致，便于模型建立映射。
- **错误处理**：出错时可在 `tool_result.status` 标记 `"error"`，并提供 `error_code` / `message`；模型据此决定是否重试。
- **Streaming 模式**：当使用流式接口时，你需要在收到 `tool_calls` 完整结束的事件后再发送对应 `tool` 消息，否则服务端会持续等待。

## 完成情况
- ✨ 提供了 role=tool 消息的字段规范与常见写法。
- ✨ 给出可插入 Notification 的 JSON 模板及整合建议。
- ✅ Build/Lint/Tests: PASS（本次仅文档说明，无需执行）。

如果你后续想把 Notification 做成通用中间件，或者希望模型在无人工具调用时也轮询环境，可以继续展开，我可以帮你设计事件队列或节流策略。
