# Phase 2 Findings: view_file Micro-Wizard

**状态**：Completed first successful validation
**日期**：2026-05-17

---

## 1. 结论

收缩版 Phase 2 的核心机制已经在真实 DeepSeek run 中跑通：

```text
main timeline: assistant calls view_file(README.md)
side timeline: full view_file result + assistant prefix
side timeline: assistant calls select_remember(ranges: 10-16, 19-35)
main timeline: restored to wizard_start + reduced view_file tool result
main timeline: assistant answers from reduced result
```

关键实验结果：

- 旁路时间线中模型成功调用了 `select_remember`。
- 主时间线后续请求只包含 reduced view，不包含完整 README。
- 最终回答正确，给出了 `WidgetOptions.Timeout`、`WidgetRetryPolicy.RetryCount` / `Delay` 和 `WidgetClient(options, retryPolicy)` 示例。
- 审阅后补充了 `select_remember.path/intention` 与最近一次 `view_file` 的匹配校验，避免 stale/wrong selection 改写主时间线。

这初步证明了“先看完整内容，再由模型自己选择性保留，最后回溯成智能工具结果”的 micro-wizard 技术路线可行。

---

## 2. 已完成内容

- `Phase2/Model`
  - `NumberedFileView`
  - `NumberedLine`
  - `LineRange`
  - `SelectedFileMemory`
- `Phase2/Tools`
  - `ViewFileToolLogic`
  - `SelectRememberToolLogic`
- `Llm/ChatHistory`
  - 普通 Chat history client
  - system/user/assistant/tool message
  - assistant `reasoning_content`
  - native server `tool_calls`
- `fixtures/phase2-widget-workspace`
  - 小型 WidgetClient 测试 workspace
- CLI
  - `phase2-fake-wizard`
  - `phase2-llm-wizard`

---

## 3. 验证命令

已通过：

```bash
dotnet build prototypes/MutableContextAgentProto/MutableContextAgentProto.csproj
dotnet run --project prototypes/MutableContextAgentProto -- phase2-fake-wizard
dotnet run --project prototypes/MutableContextAgentProto -- phase2-llm-wizard
```

真实 run 输出摘要：

```text
== phase2 llm step 1 ==
view_file README.md: rewritten-reduced-result; fullLines=35; selected=10-16, 19-35
== phase2 llm step 2 ==
... final answer ...
Run log: .atelia/debug-logs/MutableContextAgentProto/20260517-193018-phase2-llm-wizard.jsonl
```

---

## 4. 重要踩坑

### 4.1 多 tool call 必须逐个配对

第一次真实实验中，模型可能在一个 assistant message 中发出多个 tool call。OpenAI/DeepSeek Chat 协议要求 assistant 的每个 `tool_call_id` 后面都必须紧跟对应 tool message。

Phase 2 v1 为了保持变量最小，采取策略：

- 每轮只处理第一个 `view_file`。
- 旁路时间线只回放这个单独的 assistant tool call。
- system prompt 明确要求每轮最多调用一个 `view_file`。

后续如果要支持并行 view，需要把 wizard session 扩展成 batch 模式。

### 4.2 DeepSeek reasoner 不支持 `tool_choice=required`

旁路选择最初尝试强制 `select_remember`，服务端返回：

```text
deepseek-reasoner does not support this tool_choice
```

因此目前改为：

- `tool_choice=auto`
- 旁路只暴露 `select_remember` 一个工具
- 用 assistant prefix 强引导模型调用该工具

这次真实 run 中该策略有效。

### 4.3 assistant-prefix 续写可用

旁路时间线追加了 assistant message：

```text
我已经看到了文件的完整内容。接下来我必须调用 select_remember...
```

DeepSeek 接受了以 assistant message 结尾的上下文，并在下一次调用中继续产生 `select_remember` tool call。这个点是本阶段最有价值的正反馈。

---

## 5. 审计观察

run log 有 6 行：

```text
main-request
main-response
wizard-request
wizard-response
main-request
main-response
```

其中：

- `wizard-request` 包含完整 README 1-35 行。
- 第二个 `main-request` 只包含 selected ranges `10-16, 19-35`，并标注 omitted ranges。

这满足本阶段的关键验收：完整文件内容没有进入回溯后的主时间线，但仍可在 run log 中审计。

---

## 6. 局限

- 当前只处理第一条 `view_file` tool call，未处理同一轮多文件查看。
- 当前没有实现 retry repair；如果旁路模型不调用 `select_remember`，会 fallback 到完整结果。
- 当前选择标准只依赖模型自觉，没有最大选择比例约束。
- 当前使用虚拟行号，不具备跨编辑稳定性；后续可迁移到 `DurableText` 风格 stable block id。
- 当前 `select_remember` 是旁路工具，不进入主时间线；这是实验目标，但需要长期审计设计支撑。
- 当前 `view_file` 路径校验可阻止 `../` 逃逸，但未防御 workspace 内 symlink 指向外部路径。fixture workspace 可信，因此不阻塞本实验；若用于不可信 workspace，需要 realpath/symlink 策略。

---

## 7. 下一步建议

建议下一刀不要立刻做写文件，而是增强此实验：

1. 增加 `max_selected_ratio`，防止模型选择整个文件。
2. 增加一次 retry repair，当旁路没有调用 `select_remember` 时，用更强提示重试一次。
3. 支持一次 assistant 多个 `view_file` 的 batch wizard。
4. 把虚拟行号抽象为 `LineRef`，为后续 stable block id 留接口。
5. 再考虑接入 `replace_exact` 或 `TextEditOperation` 风格的只读后编辑实验。
