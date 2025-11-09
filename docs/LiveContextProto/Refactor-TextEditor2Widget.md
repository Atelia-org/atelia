# TextEditor2Widget LLM-first 重构规划(2025 战略版)

> **文档性质**: 本文档是 `TextEditor2Widget` 的中长期演进规划,描绘目标架构与实施路径,部分功能尚未实现。
> **核心理念**: 以「LLM 独占缓冲区 + 结构化反馈」为设计原则,专注于纯内存编辑能力。
> **职责边界**: TextEditor2Widget **仅负责独占缓存的编辑操作**,不处理持久化与同步逻辑。同步职责由 [`DataSourceBindingWidget`](./DataSourceBindingWidget.md) 承担。

> **更新记录 (2025-11-09)**:
> - 完成 Phase 1「响应契约」落地,新增结构化 Markdown 输出、状态/标志类型与核心单元测试
> - 职责分离:将持久化与同步功能拆分到独立的 DataSourceBindingWidget

> **兼容性说明**: 该组件为全新实现,暂无历史版本或遗留接口,本文描述即为唯一契约。

---

## 1. 战略定位与背景

### 1.1 转向动因
`TextEditorWidget` 与 `TextReplacementEngine` 的传统架构已难以满足以下需求:
- **多匹配场景**: 需要向 LLM 展示候选并支持交互式确认
- **状态可见性**: 需要明确的状态机管理编辑流程(待确认、多匹配选择等)
- **职责分离**: 需要将编辑逻辑与同步/持久化逻辑解耦,降低组合复杂度

因此,`TextEditor2Widget` 作为新一代组件,**专注于独占缓存的编辑能力**。持久化、外部冲突等同步职责由 [`DataSourceBindingWidget`](./DataSourceBindingWidget.md) 独立承担。

### 1.2 迁移策略
- **旧组件冻结**: `TextEditorWidget` 与 `TextReplacementEngine` 进入维护状态,仅修复高优先级缺陷,不再新增功能
- **逐步过渡**: 现有调用方通过适配层(shim)转调新组件,待覆盖率达标后规划旧组件下线
- **能力内聚**: 多匹配、虚拟选区等编辑逻辑在 `TextEditor2Widget` 内实现;持久化与同步逻辑在 `DataSourceBindingWidget` 内实现

### 1.3 设计原则
1. **LLM-first 响应**: 所有工具返回必须包含 `summary`(发生了什么)、`guidance`(下一步做什么)、`candidates`(可选的候选项)
2. **独占可校验缓存**: Widget 内部缓存是唯一权威数据源,外部只能通过 `IExclusiveBuffer` 接口读取或请求更新
3. **显式状态机**: 通过 `WorkflowState` 与 `Flags` 控制工具可见性,防止误操作
4. **编辑与同步解耦**: 编辑操作不受外部数据源状态影响,不会被持久化流程阻塞或打断
5. **分层诊断**: 业务响应保持简洁,调试细节通过 `DebugUtil` 写入分类日志

### 1.4 与 DataSourceBindingWidget 的分工
| 组件 | 职责 | 核心工具 |
| --- | --- | --- |
| `TextEditor2Widget` | 独占缓存的编辑操作（替换、选区确认、追加） | `_replace`, `_replace_selection`, `_append` |
| `DataSourceBindingWidget` | 缓存与下层数据源的同步（提交、刷新、冲突处理） | `_flush`, `_refresh`, `_diff`, `_accept_source` |
| `Buffer ↔ Sync Contract` | 统一版本号、事件、结果与工具矩阵契约 | [文档链接](./Buffer-Sync-Contract.md) |

两者通过 `IExclusiveBuffer` 接口协作,由外部调度者（如 `MemoryNotebookApp`）统一调度双 pass 循环（Update + Render）。

---

## 2. 技术背景与依赖

### 2.1 工具注册机制 (`MethodToolWrapper`)
- **自动推断**: 通过 `[Tool]` 和 `[ToolParam]` 注解自动生成工具定义,支持格式化占位符(如 `{0}_replace`)
- **类型约束**: 方法签名必须返回 `ValueTask<LodToolExecuteResult>`,最后一个参数必须为 `CancellationToken`
- **元数据生成**: 自动附加参数的必填/可选、默认值、可空性等提示信息

### 2.2 工具执行与结果 (`ToolExecutor` & `LodToolExecuteResult`)
- **分发调用**: `ToolExecutor` 根据 `toolName` 和 `toolCallId` 分发请求,记录耗时与状态
- **返回结构**: `LodToolExecuteResult` 包含两个关键字段:
  - `Status`: 类型为 `ToolExecutionStatus`,取值 `Success` / `Failed` / `Skipped`
  - `Result`: 类型为 `LevelOfDetailContent`,包含 `Basic`(简要)和 `Detail`(详细)两级内容
- **框架托管**: 工具调用 ID、执行耗时等元信息由 `ToolExecutor` 自动附加,工具实现只需关注业务逻辑

### 2.3 上下文渲染 (`AgentState` & `LevelOfDetail`)
- **历史管理**: `AgentState` 维护 Recent History,按 Observation ↔ Action 交替顺序组织
- **细节等级**: `RenderLiveContext` 为最新的 Observation 使用 `Detail` 级别,历史条目降为 `Basic`,以控制上下文长度
- **响应传递**: 工具返回的 `LodToolExecuteResult` 通过 `ToolResultsEntry` 注入历史,供下一轮模型读取

### 2.4 对 TextEditor2Widget 的意义
- **职责聚焦**: Widget 只需构造清晰的 `LodToolExecuteResult`,框架会自动处理 ID、耗时、日志等基础设施
- **一致性保证**: Markdown 输出与 `LodToolExecuteResult` 应保持信息同步,便于解析与验证
- **扩展空间**: 未来引入 `TextEditResponse` 时,可在此基础上增加结构化字段,无需重构现有逻辑

---

## 3. 现状与目标对比

### 3.1 当前实现 (Phase 1 · 2025-11)
`TextEditor2Widget` 已完成第一阶段(响应契约)的核心交付,具备以下能力:
- ✅ **双工具支持**: `_replace` 与 `_replace_selection`,并在多匹配后仅暴露确认工具
- ✅ **多匹配检测**: `_replace` 命中多处时生成虚拟选区(最多 5 个),附带候选表格
- ✅ **选区可视化**: 在快照中插入 `[[SEL#X]]` / `[[/SEL#X]]` 标记,配合图例说明
- ✅ **独占缓存**: 所有编辑仅更新内存缓冲区,不直接写回底层存储
- ✅ **结构化 Markdown 响应**: 通过 `TextEditResponseFormatter` 输出状态/指标/候选,`LevelOfDetailContent.Basic` 保持精简摘要
- ✅ **状态与标志基础**: `TextEditWorkflowState`、`TextEditFlag` 支撑 `Idle` ↔ `SelectionPending` 流程,不再参与持久化与外部冲突判断
- ✅ **单元测试覆盖**: `TextEditResponseFormatterTests` 与 `TextEditor2WidgetTests` 覆盖多匹配、选区生命周期与标志组合

### 3.2 待实现能力 (Phase 2+)
结合当前化简策略,Phase 2 起的重点工作聚焦在以下方向:
- **统一响应载体 (`TextEditResponse`)**: 将 Markdown 视为渲染结果,新增结构化 DTO 作为工具与渲染层之间的唯一契约,并在 `LevelOfDetailContent` 中直接复用。
- 🧭 **工具可见性映射表**: 引入 `TextEditToolMatrix`(命名暂定) 以配置化方式定义 `WorkflowState × Status → 可见工具/默认指引`,替代手写 `switch` 分支,确保新工具只需增量配置。
- 🧢 **操作结果统一 (`OperationResult`)**: 编辑工具通过统一结果类型返回 `ErrorCode`、`Message` 与 `DiagnosticHint`, 便于与同步组件共享 Guidance 模板。
- 🔢 **版本号策略**: 用单调递增的 `BufferVersion` 替代哈希指纹,事件负载仅携带版本号与时间戳,简化 Update pass 的脏值检测,并与 Buffer ↔ Sync 契约保持一致。
- 🧱 **双 Pass 支撑与接口落地**: 在版本号机制下完善 `IExclusiveBuffer` 实现、`Update()` 清理逻辑与 `ContentChanged` 事件,为 DataSourceBindingWidget 对接做好准备。
- 🧩 **共享 ToolMatrix/Formatter**: 与同步组件共用工具矩阵与响应 Formatter 基础设施,避免重复维护。

---

## 4. 核心概念与术语

### 4.1 响应契约基础（Phase 1 已完成）
当前版本已在 `prototypes/Agent/Text/TextEditTypes.cs` 中实现核心枚举与数据结构,并由 `TextEditResponseFormatter` 生成统一的 Markdown 输出。为避免与同步组件混淆,下文先列出当前实际返回的取值,随后给出完整的类型定义,其余成员仅作为后续扩展预留。

#### 4.1.1 当前由 TextEditor2Widget 产出的取值
- `TextEditStatus`: `Success`、`MultiMatch`、`NoMatch`、`NoOp`
- `TextEditWorkflowState`: `Idle`、`SelectionPending`
- `TextEditFlag`: `None`、`SelectionPending`

这些取值完全由编辑 Widget 内部状态机控制,不会因下层数据源状态发生变化,也不与 `DataSourceBindingWidget` 共享存储。完整枚举定义如下,其中带有 `Persist*`、`OutOfSync`、`ExternalConflict` 等成员用于后续阶段的扩展,当前实现不会触发这些预留取值。

```csharp
// 核心类型定义节选 — 文件: prototypes/Agent/Text/TextEditTypes.cs
public readonly record struct TextEditMetrics(int Delta, int NewLength, int? SelectionCount);

public sealed record TextEditCandidate(
    int Id,
    string Preview,
    string MarkerStart,
    string MarkerEnd,
    int Occurrence,
    int ContextStart,
    int ContextEnd
);

public enum TextEditStatus {
    Success,
    NoMatch,
    MultiMatch,
    NoOp,
    PersistFailure,
    ExternalConflict,
    Exception
}

public enum TextEditWorkflowState {
    Idle,
    SelectionPending,
    PersistPending,
    OutOfSync,
    Refreshing
}

[Flags]
public enum TextEditFlag {
    None             = 0,
    SelectionPending = 1 << 0,
    PersistPending   = 1 << 1,
    OutOfSync        = 1 << 2,
    SchemaViolation  = 1 << 3,
    PersistReadOnly  = 1 << 4,
    ExternalConflict = 1 << 5,
    DiagnosticHint   = 1 << 6
}
```

#### 4.1.2 Markdown 字段语义

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `status` | `TextEditStatus` | 单次操作的即时结果,在编辑域仅会落在 4.1.1 所述四种取值 |
| `workflow_state` | `TextEditWorkflowState` | Widget 的持久状态,当前仅有 `Idle` / `SelectionPending` |
| `summary` | `string` | 单行结论,建议使用 `[OK]` / `[Warning]` 等视觉符号 |
| `guidance` | `string?` | 下一步操作建议,可为 `null` |
| `metrics` | `TextEditMetrics` | 包含 `delta`、`new_length`、`selection_count?` |
| `candidates` | `TextEditCandidate[]?` | 多匹配时的候选列表,单匹配时为 `null` |
| `flags` | `TextEditFlag` | 由 `workflow_state` 与操作结果派生的位标志枚举,无标志时为 `TextEditFlag.None` |

> Formatter 要点: `TextEditResponseFormatter` 固定输出「状态头部 → 概览 → 指标 → 候选」四段 Markdown,并依据 `TextEditStatus` 自动选择 `[OK]` / `[Warning]` / `[Fail]` 视觉标签。`LevelOfDetailContent.Basic` 仅保留 `summary + guidance`, 详细信息位于 `Detail`。

> 后续规划: 计划在 Phase 2 引入 `TextEditResponse` 记录类型,以 JSON 形式补充给其他前端/Agent。现阶段的 Markdown 格式已可作为该结构的序列化参考。

#### 4.1.3 结构化响应载体 (Phase 2 执行项)

Phase 2 将新增 `TextEditResponse` 记录类型,其字段与 4.1.2 所述 Markdown 字段一一对应,并作为以下组件的共同输入/输出:

```csharp
public sealed record TextEditResponse(
    TextEditStatus Status,
    TextEditWorkflowState WorkflowState,
    TextEditFlag Flags,
    string Summary,
    string? Guidance,
    TextEditMetrics Metrics,
    IReadOnlyList<TextEditCandidate>? Candidates
);
```

- 工具执行完成后返回 `TextEditResponse`,再由统一 Formatter 负责生成 Markdown 与 `LevelOfDetailContent`。
- `Basic` 视图将直接引用 `Summary`/`Guidance`,避免字符串重新拼装。
- 与 DataSourceBindingWidget 的响应 DTO 对齐,方便未来在 IApp 中合并展示。

> **落地步骤**: (1) 定义 DTO 与快照测试; (2) 修改工具返回逻辑; (3) 将 Markdown Formatter 改写为 `TextEditResponseFormatter.Render(TextEditResponse response)`。

#### 4.1.4 OperationResult 与 Guidance 模板（Phase 2 执行项）

- `TextEditor2Widget` 所有编辑工具在内部通过 `OperationResult` 汇报成功、错误码与诊断提示,最终映射为 `LodToolExecuteResult`。
- Guidance 模板按照 `ErrorCode` 进行配置,与 `DataSourceBindingWidget` 共用同一套字典,确保用户提示一致。
- `OperationResult` 的结构、常见错误码以及诊断字段要求详见 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md)。
- 所有失败路径必须写入 `TextEdit.MatchTrace` 或 `TextEdit.Schema` 日志,并将 `OperationId` 附带给同步组件。

### 4.2 状态机定义（Phase 1 小步落地,Phase 2 简化）

| WorkflowState | 描述 | 典型转入条件 |
| --- | --- | --- |
| `Idle` | 缓存空闲,可接受任意编辑操作 | 单匹配替换成功或选区确认完成 |
| `SelectionPending` | 多匹配待确认,等待 `_replace_selection` | `_replace` 检测到多处匹配 |

> **Phase 2 简化**: 本 Widget 不再负责 `PersistPending`、`OutOfSync`、`Refreshing` 等同步相关状态,这些职责转移到 `DataSourceBindingWidget`。
> **当前实现情况**: Phase 1 已在 `TextEditor2Widget` 内部维护 `_workflowState`,覆盖 `Idle`、`SelectionPending` 两种路径,与 4.1.1 的取值保持一致。

### 4.3 标志位映射（Phase 2 简化）

| WorkflowState | 必含 Flags | 可选 Flags | Guidance 重点 |
| --- | --- | --- | --- |
| `Idle` | `None` | `DiagnosticHint` | 告知当前可执行操作 |
| `SelectionPending` | `SelectionPending` | `DiagnosticHint` | 引导调用 `_replace_selection` |

**注**: 状态切换时需同步更新 Flags,确保 Markdown、JSON、日志三者一致。

> **Phase 2 简化**: 本 Widget 不再产出 `PersistPending`、`OutOfSync`、`PersistReadOnly`、`ExternalConflict` 等同步相关标志,这些位由 `DataSourceBindingWidget` 决定是否在最终呈现中补充。

### 4.4 诊断分类 (`DebugUtil` 类别)

| 类别 | 用途 |
| --- | --- |
| `TextEdit.MatchTrace` | 记录所有匹配位置、选区上下文与快照版本(或指纹),用于回放决策 |
| `TextEdit.Schema` | 记录响应结构失效或解析失败时的原始 Markdown/JSON |

环境变量 `ATELIA_DEBUG_CATEGORIES` 控制输出,调试模式下 Guidance 可提示查看对应日志。

> **Phase 2 简化**: 移除 `TextEdit.Persistence`、`TextEdit.External` 类别,这些由 `DataSourceBindingWidget` 的 `Sync.*` 类别承担。

---

## 5. 典型交互流程

### 5.1 单匹配替换 (已实现)
1. LLM 调用 `_replace(old_text="foo", new_text="bar")`
2. Widget 检测到唯一匹配,直接执行替换
3. 返回 `status=Success`,`delta=0`,`new_length=1234`,`workflow_state=Idle`

### 5.2 多匹配确认 (已实现)
1. LLM 调用 `_replace(old_text="foo", new_text="bar")`
2. Widget 检测到 3 处匹配,生成虚拟选区(最多 5 个)
3. 返回 `status=MultiMatch`,`selection_count=3`,`workflow_state=SelectionPending`,附带候选表格
4. LLM 查看快照中的 `[[SEL#1]]...[[/SEL#1]]` 标记,选择目标选区
5. LLM 调用 `_replace_selection(selection_id=2)`
6. Widget 执行选定替换,返回 `status=Success`,`workflow_state=Idle`

### 5.3 编辑与同步协作 (由外部调度者统一管理)
1. LLM 通过 TextEditor2Widget 完成编辑操作(缓存已修改)
2. DataSourceBindingWidget 在 UpdateAsync() 中检测到缓存版本变化
3. DataSourceBindingWidget 提示 LLM "缓存有未提交修改,调用 `_flush` 提交"
4. LLM 调用 DataSourceBindingWidget 的 `_flush` 工具（同步逻辑完全在 DataSourceBindingWidget 内执行）
5. 同步成功后,DataSourceBindingWidget 状态回到 `Synced`

### 5.4 工具可见性映射 (Phase 2 上线)

- 引入 `TextEditToolMatrix` (命名暂定),以配置化方式维护 `WorkflowState × Status → {Tools, Guidance}` 映射,默认值覆盖 `Idle` 与 `SelectionPending` 两类状态。
- `_replace` 等基础工具默认始终可见,但在 `SelectionPending` 状态下由映射显式设置为隐藏,避免重复暴露。
- 对新增工具(例如 `_append`,`_discard_selection`) 仅需新增映射配置,并通过快照测试验证 `VisibleToolsFor(state)` 输出。
- IApp 读取工具列表时可直接使用映射提供的只读结果,确保 UI 与 Widget 内部逻辑一致。

> **测试策略**: 针对重点状态组合(`Idle+Success`,`SelectionPending+MultiMatch` 等) 生成最小快照,断言可见工具集合与 Guidance 文案,取代多处条件判断测试。

---

## 6. Markdown 响应规范

### 6.1 结构定义
所有返回正文采用半结构化 Markdown,固定包含四个段落(按顺序):

#### 6.1.1 状态头部 (必填)
三行固定顺序: `status` → `state` → `flags`,字段值置于内联代码。无标志时输出 `flags: -` (即 `TextEditFlag.None`)。

```markdown
status: `Success`
state: `Idle`
flags: -
```

#### 6.1.2 概览 (必填)
以 `### [OK] 概览`、`### [Warning] 概览`、`### [Fail] 概览` 之一开头,列表项键名固定为 `summary` 与 `guidance`。缺省时写"(留空)"。

```markdown
### [OK] 概览
- summary: 已完成替换,缓存已更新
- guidance: (留空)
```

#### 6.1.3 指标 (必填)
以 `### [Metrics] 指标` 开头,表头固定为 `指标` / `值`,缺失值用 `-` 占位。常见指标包括 `delta`、`new_length` 与 `selection_count`。

```markdown
### [Metrics] 指标
| 指标 | 值 |
| --- | --- |
| delta | +12 |
| new_length | 1824 |
| selection_count | - |
```

#### 6.1.4 候选选区 (可选)
存在虚拟选区时输出 `### [Target] 候选选区` 表格,列顺序固定为 `Id`、`MarkerStart`、`MarkerEnd`、`Preview`、`Occurrence`、`ContextStart`、`ContextEnd`。

```markdown
### [Target] 候选选区
| Id | MarkerStart | MarkerEnd | Preview | Occurrence | ContextStart | ContextEnd |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `[[SEL#1]]` | `[[/SEL#1]]` | `...Foo() {` | 0 | 118 | 150 |
| 2 | `[[SEL#2]]` | `[[/SEL#2]]` | `...Foo() {` | 1 | 312 | 344 |
```

### 6.2 示例: 多匹配响应

```markdown
status: `MultiMatch`
state: `SelectionPending`
flags: `SelectionPending`

### [Warning] 概览
- summary: 检测到 3 处候选
- guidance: 调用 `_replace_selection` 选择目标编号，或重新发起更精确的 `_replace`

### [Metrics] 指标
| 指标 | 值 |
| --- | --- |
| delta | 0 |
| new_length | 1824 |
| selection_count | 3 |

### [Target] 候选选区
| Id | MarkerStart | MarkerEnd | Preview | Occurrence | ContextStart | ContextEnd |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | `[[SEL#1]]` | `[[/SEL#1]]` | `...Foo() {` | 0 | 118 | 150 |
| 2 | `[[SEL#2]]` | `[[/SEL#2]]` | `...Foo() {` | 1 | 312 | 344 |
| 3 | `[[SEL#3]]` | `[[/SEL#3]]` | `...Foo() {` | 2 | 521 | 553 |
```

### 6.3 Formatter 实现建议

从 Phase 2 起,Formatter 将作为公共工具(`WidgetResponseFormatter`)的一部分,面向 `TextEditResponse` 返回 Markdown 与 `LevelOfDetailContent` 双产物:

```csharp
public static LevelOfDetailContent Render(TextEditResponse response)
{
    var markdown = MarkdownTemplates.TextEdit(response);
    var basic = string.Join('\n', new[]
    {
        $"status: `{response.Status}`",
        $"state: `{response.WorkflowState}`",
        response.Guidance is { Length: > 0 } guidance
            ? $"guidance: {guidance}"
            : "guidance: (留空)"
    });
    return new LevelOfDetailContent(basic, markdown);
}
```

- `MarkdownTemplates.TextEdit` 统一拼装状态头部、指标表格与候选表,并复用同一套 CSS-less 样式。
- 与 DataSourceBindingWidget 共用 Formatter 后,一处修改即可影响双侧输出,降低维护成本。
- 渲染模板可搭配快照测试与 schema 校验,确保 DTO 字段和 Markdown 结构保持一致。

> Formatter 仅处理编辑领域指标。涉及同步/差异的 Markdown 和 JSON 由 `DataSourceBindingWidget` 的专属模板负责,但仍可共用底层渲染 Helper。

### 6.4 覆盖要求
- `TextEditResponseFormatterTests` 需覆盖 Idle、SelectionPending、SchemaViolation 等典型分支。
- `TextEditor2WidgetTests` 在多匹配、候选确认、选区失效等场景下断言 Markdown 快照与 `LevelOfDetailContent.Basic`/`Detail` 保持一致。

## 7. 双 Pass 支持与 IExclusiveBuffer

### 7.1 IExclusiveBuffer 接口
`TextEditor2Widget` 对外实现以下接口，供同步组件订阅：

```csharp
public interface IExclusiveBuffer
{
    string GetSnapshot();
    string GetVersion();
    ValueTask<OperationResult<bool>> TryUpdateContentAsync(string newContent, CancellationToken ct);
    event EventHandler<BufferChangedEventArgs> ContentChanged;
}
```

- `GetVersion()` 返回单调递增的 `BufferVersion`（字符串化的整数）,配合 `ContentChangedEventArgs.Version` 实现 O(1) 脏值检测。
- `TryUpdateContentAsync` 仅在绑定组件执行刷新或接受外部变更时调用；返回 `OperationResult<bool>`，失败时必须提供 `ErrorCode`、`Message` 与 `DiagnosticHint` 并保持原状态。
- 在 `_replace`、`_replace_selection`、`_append` 等操作成功后触发 `ContentChanged`,事件参数携带 `Version`、`Timestamp` 与基础指标(例如 delta、selectionCount)。
- `ContentChanged` 事件必须携带最新版本号,并遵循 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md) 中的字段约定,以便同步组件在 Update pass 中无损判脏。

> **外部写入契约**: 任何来自同步域或其他组件的内容写入都必须通过 `TryUpdateContentAsync` 进入缓存,Widget 保留拒绝权以守住独占语义。禁止直接改写内部缓冲区或绕过事件流。

### 7.2 Update/Render Pass

```csharp
public class TextEditor2Widget : IExclusiveBuffer
{
    public ValueTask UpdateAsync(CancellationToken ct = default)
    {
        ExpireSelectionsIfSnapshotChanged();
        return ValueTask.CompletedTask;
    }

    public LevelOfDetailContent Render()
    {
        var basic = RenderBasicStatus();
        var detail = RenderDetailedView();
        return new LevelOfDetailContent(basic, detail);
    }
}
```

> Update pass 主要承担内部 housekeeping：清理过期虚拟选区、滚动版本计数、回收临时状态等。所有外部同步决策由 `DataSourceBindingWidget` 负责，TextEditor2Widget 不直接访问下层数据源；版本与事件字段需符合 [`Buffer-Sync-Contract`](./Buffer-Sync-Contract.md) 约定。

> **职责提醒**：`UpdateAsync` 与 `Render()` 不应从内部直接调用同步组件或访问底层数据源，以免打破“编辑独占缓存”的契约。任何跨层协作都由外层调度者串联完成。

> **统一顺序调度**：外部 orchestrator 应在每轮 LLM 调用前按 `TextEditor2Widget.Update → DataSourceBindingWidget.Update → TextEditor2Widget.Render → DataSourceBindingWidget.Render` 的顺序执行，确保缓存状态与同步状态在同一帧内保持一致，避免互相等待或竞争写入。

## 8. 虚拟选区与候选编号
- `_replace` 多匹配时缓存 `SelectionState`：`(snapshotHash, needle, defaultReplacement, entries[])`。
- 每个候选分配递增 `Id` 并在快照中插入 `[[SEL#X]]`/`[[/SEL#X]]` 标记，`Occurrence` 记录匹配顺序，便于后续校验。
- `_replace_selection` 执行前需校验：
  1. 当前指纹与 `SelectionState` 的 `snapshotHash` 一致；
  2. 目标位置仍匹配原始 needle。
- 任意成功写入或刷新都会清除旧 `SelectionState`，Guidance 应提醒“选区已作废，需要重新匹配”。

## 9. 测试策略
- **状态机单测**：验证 Idle → SelectionPending → Idle 的核心转换，覆盖多匹配/无匹配/选区失效。
- **Formatter 快照**：针对 Success、MultiMatch、SchemaViolation 等输出维持快照测试，确保 Markdown 模板稳定。
- **接口契约测试**：Mock `IExclusiveBuffer` 调用，验证 `ContentChanged` 事件、`TryUpdateContentAsync` 拒绝路径，以及双 pass 对版本号的维护。
- **协作冒烟**：与 `DataSourceBindingWidget` 的最小集成测试，只关注事件 + 版本号流转是否达成约定。测试场景应通过 `IExclusiveBuffer` 接口模拟外部刷新/更新请求，验证事件通知与拒绝逻辑符合预期。

## 10. 诊断与日志
- 使用 `DebugUtil.Print("TextEdit.MatchTrace", ...)` 记录匹配详情、虚拟选区范围和操作耗时。
- 在检测到响应结构异常时写入 `TextEdit.Schema` 分类，便于与同步组件日志区分。
- 编辑侧不再输出持久化或外部冲突相关日志；这些信息由 `DataSourceBindingWidget` 的 `Sync.*` 分类承载，可在相应日志中注明来源以便关联历史上的 `TextEdit.Persistence` / `TextEdit.External` 记录。

## 11. 风险与缓解
| 风险 | 说明 | 缓解措施 |
| --- | --- | --- |
| 选区漂移 | 替换间隔过长导致上下文变化 | 在 Update pass 中校验版本号(或快照指纹),不一致时自动清理并提示重新生成候选 |
| SchemaViolation | Markdown 模板被意外修改 | 维护快照测试、在 Formatter 中做字段缺失兜底，并输出 `TextEdit.Schema` 日志 |
| 版本号不同步 | 内部版本未及时递增或事件负载缺失 | 统一通过 `BufferVersion` 管理,成功写入后立即递增并落盘到事件,配套快照测试 |
| 接口演进 | `IExclusiveBuffer` 签名变化影响绑定组件 | 建立接口版本说明，修改时同步更新 DataSourceBindingWidget 文档与集成测试 |

## 12. 实施路线图
| 阶段 | 目标 | 关键交付 |
| --- | --- | --- |
| Phase 1 ✅ | 响应契约 | Formatter、基础状态机、单元测试已落地 |
| Phase 2 | 统一响应 & 可见性矩阵 | 定义 `TextEditResponse` DTO、迁移共享 Formatter、落地 `TextEditToolMatrix`、启用版本号事件 |
| Phase 3 | 接口与版本号 | 完成 `IExclusiveBuffer`、`ContentChanged` 事件及 `BufferVersion` 递增逻辑,对接 DataSourceBindingWidget |
| Phase 4 | 工具扩展 | 引入 `_append` / `_discard_selection` 等辅助工具,同时复用映射配置 |
| Phase 5 | 体验打磨 | 调整 Render 输出样式、补充示例对话与调试脚本 |

## 13. 附录 A: 术语对照表

| 术语 | 说明 |
| --- | --- |
| `LodToolExecuteResult` | 工具返回类型,包含 `Status` (ToolExecutionStatus) 和 `Result` (LevelOfDetailContent) |
| `ToolExecutionStatus` | 工具执行状态枚举: `Success` / `Failed` / `Skipped` |
| `LevelOfDetailContent` | 双级内容容器,包含 `Basic`(简要)和 `Detail`(详细)两级文本 |
| `TextEditWorkflowState` | 编辑侧状态机,现阶段仅 `Idle` 与 `SelectionPending` |
| `TextEditFlag` | 位标志枚举,当前使用 `None` 与 `SelectionPending` 两值 |
| `SelectionState` | 多匹配时的虚拟选区缓存 |
| `IExclusiveBuffer` | 由 TextEditor2Widget 实现的独占缓存接口 |
| `DataSourceBindingWidget` | 专注同步职责的配套组件 |

---

**文档版本**: 2025-战略版-v2.1 (职责分离版)
**最后更新**: 2025年11月9日
**维护者**: Atelia 开发团队

本战略版文档明确了 `TextEditor2Widget` 专注于独占缓存编辑能力的设计理念，通过与 [`DataSourceBindingWidget`](./DataSourceBindingWidget.md) 的职责分离，实现编辑与同步的完全解耦，为后续实现、测试与迭代提供清晰的架构指引。
