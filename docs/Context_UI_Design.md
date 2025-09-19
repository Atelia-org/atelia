# Context UI：面向 LLM 的上下文注入式界面（设计与需求）

> 目的：抽象出一套可复用的、面向 LLM 的“上下文即界面”的交互模式与渲染管线，使 MemoTree 及扩展数据源能稳定、可控地向 LLM 呈现“信息+可操作入口（Action Tokens）”，并由 MemoTree 负责把文本标识映射回内存实体与工具调用。

## 背景与动机

- LLM 在会话中是“短时上下文、无持久记忆”的；它“只知道上下文里出现过的信息”。
- 因此，不能依赖“用户/模型记忆”，需要在每次渲染中显式注入：
  - 可用视图列表与简要说明（Description/Note），
  - 当前环境/元信息，
  - 树/代码等内容的“可控摘要”，
  - 以及“可操作节点”的机器可解析标识。
- 我们把这种“通过 Markdown 上下文直接呈现 UI、并附带操作入口”的方式称为 Context UI。

## 目标与非目标

- 目标
  - G1：统一的“渲染→上下文注入”管线，可聚合多数据源（本地 .memotree、环境、Roslyn、插件…）。
  - G2：为可操作对象提供统一的 Action Token（文本标识）规范，稳定可解析、低歧义。
  - G3：在 Markdown 顶部呈现“视图面板（当前视图 + 其他视图 + 说明 + 操作提示）”。
  - G4：可配置的预算/优先级与裁剪策略，保障上下文可控与核心信息优先。
  - G5：可扩展的 Source 插件模型（注册、优先级、失败降级）。
- 非目标
  - N1：不要求一次性把全部内容注入上下文；鼓励分层与按需展开。
  - N2：不绑定具体的工具协议（支持 System.CommandLine/函数调用/自定义桥接）。

## 核心概念

- View / ViewState：用户关注的“视图”，含 Name + Description（简要说明）。
- Render Source：产出一块 Markdown 的生产者（如 Views 面板、Workspace 树、Env、Roslyn）。
- Render Context：一次渲染的上下文数据（当前视图、选项、预算、服务访问句柄）。
- Render Section：单个 Source 产出的 Markdown 片段（带标题/权重）。
- Action Token：附着于可操作对象的统一标识文本，用于 LLM 发起工具调用时的引用。

## Action Token 规范（初版）

- 目标：机器可解析、与正文区分、可读性尚可、纯 ASCII、避免碰撞。
- 形态：`[[MT:Node:<NodeId>]]`、`[[MT:View:<ViewName>]]`（前缀 MT=MemoTree）。
- NodeId/Name 值：
  - NodeId 使用现有 NodeId 字符串（保持 CJK 不转义）。
  - ViewName 使用实际视图名（同上）。
- 正则建议：
  - Node：`\\[\\[MT:Node:([\\w\\-\\u0080-\\uffff]+)\\]\\]`
  - View：`\\[\\[MT:View:([\\w\\-\\u0080-\\uffff]+)\\]\\]`
- 放置方式：
  - 节点标题行尾附：`… [<Title>] [<NodeId>] [[MT:Node:<NodeId>]]`
  - 视图面板列表中为每个视图行附 `[[MT:View:<Name>]]`
- 解析与映射：MemoTree 的“Action Router”负责扫描上下文中的 Token → 校验存在性 → 映射为内存实体 → 触发工具调用。

## 渲染管线（两层结构）

- IRenderSource
  - Id: string; Title: string; Priority: int
  - ProduceAsync(RenderContext ctx) → RenderSection
- RenderContext
  - CurrentViewName, ViewState
  - Services: IViewStateStorage, ICognitiveNodeStorage, 配置/路径等
  - Budgets: TotalToken/Chars, PerSourceQuota
  - Mode: Normal/Debug
- RenderSection
  - Key, Title, Markdown, Weight
- 合并策略
  - 按 Priority 产出 Section → 按预算与 Weight 合并、必要时裁剪（保留 Meta/Env 优先）。

## 首批内置 Sources（建议）

1) ViewMetaSource（视图元信息面板）
- 展示：当前视图（Name + Description + [[MT:View:Name]]）
- 列表：其他最近 N 个视图（Name + Description + LastModified + Token）
- 提示：如何 switch/rename/delete（简短示例）

2) EnvInfoSource（环境/元信息）
- 时间戳、工作区根/Link 状态、版本、统计摘要（节点总数、展开数…）

3) WorkspaceSource（本地 .memotree 树）
- 复用现有树渲染；展开节点在标题行尾附 `[[MT:Node:<Id>]]`
- 预算：限制层级、节点数、字符数

4) CodebaseSource（预留：Roslyn）
- 解决方案结构/类型索引的概要视图（后续接入）

5) PluginSource（扩展点）
- 第三方/场景化源（如游戏/外部系统）；注册为 IRenderSource 即可

## 预算与裁剪

- 全局预算：字符/Token 上限（对齐调用平台约束）。
- 配额策略：Meta > Env > 概览 > 详细；树内容按深度/顺序截断。
- 裁剪提示：在文末插入“已裁剪说明 + 如何请求更多内容”。

## 安全与健壮性

- Token 防碰撞：唯一前缀 MT:，严格正则；对用户输入进行转义。
- 注入防护：Source 输出统一转义/规范化；避免把敏感路径/秘钥放入上下文。
- 容错：单个 Source 失败不阻塞整体（降级/跳过 + 记录）。

## 版本化与调试

- 协议版本：在 Meta/Env 中注明 Context UI 版本（v0, v1…）。
- Debug 模式：追加统计与诊断信息（默认关闭）。

## 与命令的关系

- 命令仍保留（switch/rename/delete/list），但“可用视图”等关键信息通过渲染面板主动呈现，降低 LLM 的隐式知识假设。

## 增量落地（M1→M5）

- M1：ViewState 增加 Description；RenderViewAsync 顶部插入 Views 面板（含 Tokens）。
- M2：抽象 IRenderSource/RenderContext/RenderSection；把 Views 面板与树渲染各做成 Source。
- M3：新增 EnvInfoSource；加入总预算与简单裁剪。
- M4：完善预算/权重/裁剪策略；错误降级与 Debug 开关。
- M5：接入 Roslyn/插件机制；完善 Action Router，打通工具调用闭环。

## 开放问题（需进一步讨论）

- Token 形态是否需要更“显眼”或“更不打扰阅读”？（如使用特殊边界符号）
- 要不要给 Node/View 之外的对象（关系、查询、过滤器）也定义 Token？
- 预算单位使用字符还是 Token（依赖运行平台/模型）？
- 命令提示的语言与长度如何权衡（中/英，简洁性 vs 明确性）？

---

本文档为可迭代草案。建议先按 M1 实施并观察 LLM 行为，再逐步管线化与扩展数据源。
