# Phase 2 Design: view_file Micro-Wizard

**状态**：Draft v0.1
**目标**：验证“先完整观察，再由同一个模型选择性记忆，最后把主时间线回溯成只看到节选结果”的 micro-wizard 是否可行。

---

## 1. 收缩后的实验目标

Phase 2 不再同时推进“单 user message 投影 + 文本编辑 + Memory Notebook”。

新的目标是：

> 在普通 Chat tool-loop 中，为 `view_file` 绑定一个 micro-wizard 状态机，让模型读取文件后必须选择要保留的行段，然后把主上下文回溯到 `view_file` 之前，并用节选内容替代完整文件内容作为工具结果。

这个实验只验证一件事：

**LLM 自己能否在旁路时间线中完成“看完整文件 -> 选择性保留 -> 让主时间线只接触保留片段”的过程。**

---

## 2. 为什么先不耦合 Phase 1 的 user-only context

Phase 1 已经证明单 `user` message tool-loop 可行，但 Phase 2 的变量更多：

- 文件查看工具
- 行号/区间选择
- 回溯上下文
- assistant 输出编辑
- select_remember 引导
- 完整结果替换为节选结果

因此 Phase 2 应先使用普通 Chat history 回灌，包括服务器返回的 assistant/tool 消息和 `reasoning_content`。这样如果实验失败，问题更可能集中在 micro-wizard 机制，而不是上下文投影格式。

---

## 3. 核心时间线

### 3.1 主时间线

```text
system/user: 任务与 workspace 说明
assistant: 调用 view_file(intention, path)
```

此时保存一份上下文快照：

```text
wizard_start = context before appending view_file tool result
```

### 3.2 旁路选择时间线

正常追加完整工具结果：

```text
tool(view_file): 带虚拟行号的完整文件内容
assistant-prefix: 我已经看到了文件内容，接下来我需要调用 select_remember 选择要保留的内容。
```

然后请求模型续写。预期模型调用：

```text
select_remember(path, intention, ranges, notes)
```

### 3.3 回溯主时间线

如果 `select_remember` 成功：

```text
context = wizard_start
append assistant(view_file tool_call)
append tool(view_file): 只包含被选择的行段 + notes + 省略说明
continue normal loop
```

从主时间线模型视角看，`view_file` 像一个智能工具：它根据 `intention` 返回了刚好相关的内容。旁路时间线中的完整文件和选择过程不会进入主上下文。

---

## 4. 工具形状

### 4.1 view_file

```csharp
string view_file(string intention, string path);
```

参数：

- `intention`：必须填写。说明为什么要看这个文件，以及要找什么信息。
- `path`：相对 workspace root 的路径。

返回：

```text
File: src/Foo.cs
Intention: 了解 FooOptions 如何配置 timeout
Line format: <line>|<content>

1|namespace Sample;
2|
3|public sealed class FooOptions {
4|    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
5|}
```

约束：

- 使用虚拟行号，不承诺与编辑器真实行号长期稳定一致。
- 行号只在本次 view 结果中有效。
- 第一版可以限制最大文件大小，超过时直接失败或截断，并要求更具体路径。

### 4.2 select_remember

建议第一版使用最小但结构化的参数：

```csharp
void select_remember(
    string path,
    string intention,
    string[] ranges,
    string summary,
    string? notes
);
```

`ranges` 格式：

```text
["3-8", "15", "22-29"]
```

含义：

- 行号闭区间。
- 必须引用最近一次 `view_file` 返回的虚拟行号。
- 不做复杂 anchor，不做内容匹配。

返回给旁路时间线：

```text
Selected 3 ranges from src/Foo.cs. The main timeline will retain only these snippets.
```

第一版不建议让模型直接传全文片段，因为那会重新引入“复制大段文本进 tool 参数”的上下文污染。

---

## 5. 状态机

```text
Idle
  on assistant tool_call view_file
    -> CaptureWizardStart

CaptureWizardStart
  execute view_file full result
  append full tool result to side context
  append assistant prefix
    -> AwaitSelection

AwaitSelection
  if assistant tool_call select_remember matches latest view
    -> RewriteMainTimeline
  else if retry_count < max
    append stronger assistant/user repair instruction
    -> AwaitSelection
  else
    -> FallbackFullResult

RewriteMainTimeline
  restore wizard_start
  append original view_file assistant tool_call
  append reduced view_file tool result
    -> Idle

FallbackFullResult
  restore normal context with full view_file result
  record warning
    -> Idle
```

第一版可以只实现一次 retry。失败时允许回退完整结果，这样实验不会死锁。

---

## 6. 数据结构建议

### 6.1 WizardSession

```csharp
sealed record ViewFileWizardSession(
    string SessionId,
    ChatContext WizardStart,
    ToolCallRequest ViewFileCall,
    string Path,
    string Intention,
    NumberedFileView FullView,
    int RetryCount
);
```

### 6.2 NumberedFileView

```csharp
sealed record NumberedFileView(
    string Path,
    string Intention,
    IReadOnlyList<NumberedLine> Lines
);

sealed record NumberedLine(int Number, string Text);
```

这部分可以借鉴 `DurableText` 的 block 思路：长远看稳定 block id 比行号好，但 Phase 2 第一版用虚拟行号更容易验证机制。等机制成立后，再把虚拟行号替换成 stable block id。

### 6.3 SelectedMemory

```csharp
sealed record SelectedFileMemory(
    string Path,
    string Intention,
    IReadOnlyList<LineRange> Ranges,
    string Summary,
    string? Notes
);
```

这里和 `TextEditOperation` 的启发类似：不要让模型复述大段内容，而是让它提交结构化选择/操作。

---

## 7. 实验任务设计

建议构造一个小型 fixture project：

```text
workspace/
  README.md
  src/
    WidgetClient.cs
    WidgetOptions.cs
    WidgetRetryPolicy.cs
    InternalNotes.cs
```

初始任务：

```text
请阅读这个 workspace 的说明，并回答：
如何使用 WidgetClient 配置自定义 timeout 和 retry policy？请给出一段示例代码。
```

系统/用户消息直接注入目录结构：

```text
Workspace files:
- README.md
- src/WidgetClient.cs
- src/WidgetOptions.cs
- src/WidgetRetryPolicy.cs
- src/InternalNotes.cs
```

工具只提供：

- `view_file`
- `select_remember`（只在 wizard 旁路时间线暴露，或正常也暴露但系统说明只有 wizard 可用）

成功标准：

- 主时间线最终回答正确。
- 主时间线中没有完整文件内容，只有 `select_remember` 选择的行段。
- run log 中可以审计旁路时间线曾经看过完整文件。
- 模型不会明显意识到“自己被回溯了”。它只会感到 `view_file` 返回了相关片段。

---

## 8. 与 keyed memory 的关系

Phase 2 前置补强已经给 `WorkingContext` 增加 keyed memory/upsert 思路。

micro-wizard 可使用稳定 key：

```text
file_view.current
file_view.selected:{path}
```

不过第一版普通 Chat loop 不一定需要接入 `WorkingContext`。keyed memory 的价值主要在后续整合：

- selected snippets 可以覆盖同一文件旧选择。
- 文件当前理解可以用 `file:{path}:summary` 更新，而不是无限追加。
- 当 Phase 2 成功后，才能把该机制接回 Phase 1 的 mutable context renderer。

---

## 9. 主要风险

- DeepSeek 的 assistant-prefix 续写模式是否稳定，需要真实 API 验证。
- 如果 `reasoning_content` 与 `content` 的可编辑/可回灌语义在接口层不完全一致，可能需要 provider-specific wire message 支持。
- 模型可能直接回答而不调用 `select_remember`，需要 retry/repair。
- 行号区间选择可能过宽，把完整文件大部分又选回来，需要设置最大选择比例。
- 回溯替换工具结果会让审计链分叉，必须有 run log 记录旁路完整内容。

---

## 10. 建议下一步任务

1. 新增 `Phase2/` 目录，先定义 `NumberedFileView`、`LineRange`、`SelectedFileMemory`。
2. 实现 `view_file` 的本地纯函数：读取 workspace 文件并生成虚拟行号。
3. 实现 `select_remember` 的 range parser 和 reduced view renderer。
4. 实现一个 fake wizard 测试，不接 LLM，只验证“完整 view -> ranges -> reduced view”。
5. 再接 DeepSeek 普通 Chat loop 与 assistant-prefix 续写。
