# Window 装饰器移除与 LOD Sections 迁移指南

> 文档目的：记录 `IWindowCarrier` 装饰器正式移除、Window 转向 `LevelOfDetailSections` 的全局改动，便于后续更新旧文档与组件。

## 背景回顾

- **旧方案**：`AgentState.RenderLiveContext()` 在渲染阶段动态生成 Window 文本，并通过实现 `IWindowCarrier` 的包装对象（`ModelInputMessage` / `ToolResultsMessage`）传递；Provider 需要先回退到 `InnerMessage` 才能读取角色化接口。
- **痛点**：
  - Provider 与调试工具需要特殊分支处理装饰器。
  - Window 仅在运行时拼装，难以在 Append 阶段做一致性校验或缓存。
  - LOD 细粒度内容与 Window 分离，历史条目不具备完整快照。

## 新方案核心要点

1. **写入阶段注入 Window**
   - `AgentState.AppendModelInput` / `AppendToolResults` 调用 `BuildWindowSnapshot()`，将结果作为 `Full` 档位的 `"[Window]"` section 写入。
   - 任何历史条目都携带完整 Window 文本，无需额外装饰器。

2. **渲染阶段仅挑选 LOD**
   - `RenderLiveContext()` 根据条目位置选择 `Full` 或 `Summary`，不再生成 Window。
   - `ModelInputMessage`、`ToolResultsMessage` 继续实现原接口，无额外 mixin。

3. **消费方读取方式统一**
   - Provider / TUI 可通过 `sections.WithoutWindow(out Window)` 提取 Window，并把剩余 section 作为正文渲染。
   - 如果未处理 Window，Sections 仍保持完整历史内容。

## 代码变更概览

| 模块 | 旧实现 | 新实现 |
| --- | --- | --- |
| `IContextMessage` | 声明 `IWindowCarrier` | 移除接口，只保留上下文基类 |
| `State/AgentState` | 渲染时决定 Window 插入位置 | Append 阶段写入 `"[Window]"`，渲染仅选 LOD |
| `State/HistoryMessages` | Wrapper 实现 `IWindowCarrier`，过滤 `Window` 段 | Wrapper 直接返回 Sections，无需过滤 |
| `Provider/Anthropic` | 通过 `IWindowCarrier` 追加 Window | 从 `sections` 中拆分 `Window` 段 |
| `Agent/ConsoleTui` | 检测 `IWindowCarrier` 打印 Window | 从 Sections 解析 `"[Window]"` |
| `LevelOfDetailSections` | 无提取辅助 | 新增 `TryGetSection` / `WithoutWindow` 等扩展 |

## 对文档的影响

需要更新所有仍提及 `IWindowCarrier` 或“Window 装饰器”的资料，建议按优先级处理：

1. **LiveContextProto 系列文档**
   - `docs/LiveContextProto/LevelOfDetailSections.md` 已更新为 Sections 模式。
   - 其余归档/设计文档需说明 Window 直接存储为 Section 并在 Append 阶段生成。

2. **MemoFileProto 蓝图**
   - 搜索关键字 `IWindowCarrier`、`WindowDecoratedMessage`，更新为新方案或标记为历史背景。

3. **Provider 实现说明**
   - `docs/LiveContextProto/AnthropicClient/*` 需描述如何从 Sections 解析 Window。

4. **开发者指南 & 模块 README**
   - 确保没有遗留“在渲染阶段附加 Window 装饰器”的描述。

## 更新建议流程

1. **批量检索**：在仓库中搜索 `IWindowCarrier`，列出仍依赖旧语义的文件。
2. **分批改写**：
   - 将描述改为“Window 以 `Full` 档的 `"[Window]"` Section 形式存在”。
   - 添加 `LevelOfDetailSectionExtensions` 的使用示例（提取 / 过滤）。
3. **保留历史注记**：如需记录旧方案，可在文档中以“历史设计”或“废弃方案”形式保留。
4. **验证引用**：对 Provider、调试工具相关文档执行一次 README walkthrough，确认文字与现实现状一致。

## 注意事项

- Window 优先写入最新条目；旧条目仍可保留旧数据，但不再通过装饰器自动注入。
- 若未来需要多段 Window，可在 `LevelOfDetailSections` 引入额外键值（保持 `Full`/`Summary`/`Gist` 语义一致）。
- 所有新组件若需使用 Window，只需从 Sections 中按键读取，无需额外接口依赖。

---

如需进一步讨论历史方案与当前实现差异，可在后续蓝图文档中添加“迁移记录”章节并引用本文。
