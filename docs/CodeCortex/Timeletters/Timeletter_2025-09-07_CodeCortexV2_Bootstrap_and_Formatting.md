# 给过去的我：CodeCortex V2 启动与 Markdown/XMLDoc 接线（Bootstrap & Formatting）

本信作为“可编辑上下文”的回溯补丁，覆盖从 V2 启动到完成 Markdown/XMLDoc 渲染与类型/成员 Outline 打通的阶段。

## 起因
- 目标：为 LLM/TUI 友好的代码理解提供“快搜 + 结构化 Outline + Markdown 文档”的闭环体验。
- 约束：
  - Markdown-first（默认），JSON 作为选项
  - 小而清：接口稳定、文件触点最小化；细节尽量外移
  - Roslyn Workspace 作为唯一事实来源

## 关键决策（不变约束）
- 统一对外 Id：使用 DocCommentId（类型以 `T:` 前缀），而不是 SymbolKey（公共 API 不便使用）
- Provider 模式：将 Outline 提取与渲染解耦
- 复用成熟实现：移植 V1 的 MarkdownRenderer 与 XML 文档处理逻辑为 V2 友好形态

## 结果（接口与命令）
- Type/Member Outline 渲染（Markdown 默认）：
  - Providers：`TypeOutlineProvider`、`MemberOutlineProvider`
  - 渲染：`XmlDocFormatter.*` + `MarkdownRenderer`
- CLI（DevCli）：默认 Markdown 输出，`--json` 切换

## 最小文件触点
- `src/CodeCortexV2/Providers/TypeOutlineProvider.cs`
- `src/CodeCortexV2/Providers/MemberOutlineProvider.cs`
- `src/CodeCortexV2/Formatting/XmlDocFormatter.*.cs`
- `src/CodeCortexV2/Formatting/MarkdownRenderer.cs`
- `src/CodeCortexV2.DevCli/Program.cs`

## 行为快照（落地后的“体感相同”再现）
- Markdown 输出默认；列表显示仅用 DocCommentId（更精确、更短、更熟悉）
- Outline 中成员以“签名 + 摘要”要点式列出

## 设计要点
- XmlDoc → Markdown：
  - 列表/段落/代码块的排版与缩进统一；可读性优先
  - 关键字与类型名的映射按 C# 语义格式化
- Provider 解耦：Outline 的数据提取与 Markdown 渲染可独立演进

## 下一步可选分支（当时的我可选其一）
- A’：Outline 结构化增强（继承/实现接口/泛型约束，保持简洁）
- B’：Find 体验增强（`--kind` 过滤、前缀/包含索引、DebugUtil 全链路埋点）
- C’：增量索引（WorkspaceChanged 订阅，先 Project 后 Document 粒度）
- D’：成员级 DocId 支持（M:/P:/F:/E:）

## 给过去的我（操作指令）
1) 以 DocCommentId 作为一切对外 Id 与列表主显示键
2) Outline 走 Provider 渲染，保持 Markdown-first，不引入重缓存
3) 把细节交给 Timeletter 累积，“接口与命令”保持最少可复用集

