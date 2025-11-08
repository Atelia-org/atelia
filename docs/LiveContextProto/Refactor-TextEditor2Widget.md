# TextEditor2Widget LLM-first 重构规划(2025 战略版)

> **文档性质**: 本文档是 `TextEditor2Widget` 的中长期演进规划,描绘目标架构与实施路径,部分功能尚未实现。
> **核心理念**: 以「LLM 独占缓冲区 + 结构化反馈 + 安全持久化」为设计原则,打造新一代文本编辑能力。

---

## 1. 战略定位与背景

### 1.1 转向动因
`TextEditorWidget` 与 `TextReplacementEngine` 的传统架构已难以满足以下需求:
- **多匹配场景**: 需要向 LLM 展示候选并支持交互式确认
- **状态可见性**: 需要明确的状态机管理编辑流程(待确认、待提交、冲突等)
- **持久化策略**: 需要支持 Immediate/Manual/Disabled 等不同写回模式
- **外部冲突处理**: 需要检测并指引 LLM 处理底层数据变更

因此,`TextEditor2Widget` 作为新一代组件,将承载上述全部差异化能力。

### 1.2 迁移策略
- **旧组件冻结**: `TextEditorWidget` 与 `TextReplacementEngine` 进入维护状态,仅修复高优先级缺陷,不再新增功能
- **逐步过渡**: 现有调用方通过适配层(shim)转调新组件,待覆盖率达标后规划旧组件下线
- **能力内聚**: 多匹配、持久化、冲突处理等逻辑全部在 `TextEditor2Widget` 内实现,不回写旧实现

### 1.3 设计原则
1. **LLM-first 响应**: 所有工具返回必须包含 `summary`(发生了什么)、`guidance`(下一步做什么)、`candidates`(可选的候选项)
2. **独占可校验缓存**: Widget 内部缓存是唯一权威数据源,外部写入需经过显式确认
3. **显式状态机**: 通过 `WorkflowState` 与 `Flags` 控制工具可见性,防止误操作
4. **安全持久化**: 内置持久化策略、失败回退与只读模式提示
5. **分层诊断**: 业务响应保持简洁,调试细节通过 `DebugUtil` 写入分类日志

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

### 3.1 当前实现 (v0.1)
`TextEditor2Widget` 目前已实现以下基础能力:
- ✅ **双工具支持**: `_replace` 与 `_replace_selection`
- ✅ **多匹配检测**: 当 `_replace` 命中多处时,生成虚拟选区(最多 5 个)
- ✅ **选区可视化**: 通过 `[[SEL#X]]` / `[[/SEL#X]]` 标记在快照中高亮候选
- ✅ **独占缓存**: 所有编辑仅在内存缓冲区生效,不写回底层存储
- ✅ **基本反馈**: 返回 `summary`、`delta`、`new_length` 等指标

### 3.2 待实现能力 (v0.2+)
以下功能属于规划中,尚未开发:
- ⏳ **状态机管理**: 引入 `WorkflowState` 与 `Flags`,控制工具可见性
- ⏳ **持久化策略**: 支持 `Immediate` / `Manual` / `Disabled` 三种模式
- ⏳ **外部冲突检测**: 监听底层文件变更,提示 LLM 执行 `_diff` 或 `_refresh`
- ⏳ **结构化响应**: 定义 `TextEditResponse` 契约,统一字段名称与类型
- ⏳ **工具扩展**: 新增 `_commit`、`_discard`、`_diff`、`_refresh`、`_append` 等辅助工具

---

## 4. 核心概念与术语

### 4.1 响应契约 (`TextEditResponse`，待实现)
未来版本将引入结构化响应类型,包含以下字段:

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `status` | `TextEditStatus` | 单次操作的即时结果(`Success` / `NoMatch` / `MultiMatch` / `PersistFailure` / `ExternalConflict` / `Exception`) |
| `workflow_state` | `TextEditWorkflowState` | Widget 的持久状态(`Idle` / `SelectionPending` / `PersistPending` / `OutOfSync` / `Refreshing`) |
| `summary` | `string` | 单行结论,建议使用 `[OK]` / `[Warning]` / `[Fail]` 等视觉符号 |
| `guidance` | `string?` | 下一步操作建议,可为 `null` |
| `metrics` | `TextEditMetrics` | 包含 `delta`、`new_length`、`selection_count?` |
| `candidates` | `TextEditCandidate[]?` | 多匹配时的候选列表,单匹配时为 `null` |
| `flags` | `TextEditFlag` | 由 `workflow_state` 派生的位标志枚举,无标志时为 `TextEditFlag.None` |

```csharp
// 待实现的类型定义示例
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
    Idle,              // 缓存与底层同步,无挂起操作
    SelectionPending,  // 等待多匹配确认
    PersistPending,    // 缓存已修改,等待提交
    OutOfSync,         // 缓存与底层不一致
    Refreshing         // 正在刷新底层快照
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

### 4.2 状态机定义 (`WorkflowState`，待实现)

| WorkflowState | 描述 | 典型转入条件 |
| --- | --- | --- |
| `Idle` | 缓存与底层同步,无挂起事务 | 单匹配替换成功(Immediate 模式)或提交成功(Manual 模式) |
| `SelectionPending` | 多匹配待确认,等待 `_replace_selection` 或 `_discard` | `_replace` 检测到多处匹配 |
| `PersistPending` | 缓存已修改但未提交(仅 Manual 模式) | 替换成功但 `PersistMode=Manual` |
| `OutOfSync` | 缓存与底层不一致 | 提交失败或外部文件变更 |
| `Refreshing` | 正在刷新底层快照,临时禁止写入 | 调用 `_refresh` 时 |

### 4.3 标志位映射 (`Flags`，待实现)

| WorkflowState | 必含 Flags | 可选 Flags | Guidance 重点 |
| --- | --- | --- | --- |
| `Idle` | `None` | `PersistReadOnly`、`DiagnosticHint` | 告知当前可执行操作或只读模式提示 |
| `SelectionPending` | `SelectionPending` | `PersistReadOnly`、`DiagnosticHint` | 引导调用 `_replace_selection` 或 `_discard` |
| `PersistPending` | `PersistPending` | `PersistReadOnly`、`DiagnosticHint` | 提醒调用 `_commit` 或 `_discard` |
| `OutOfSync` | `OutOfSync` | `ExternalConflict`、`DiagnosticHint`、`SchemaViolation` | 禁止 `_commit`,建议先 `_diff` 再 `_refresh` |
| `Refreshing` | `DiagnosticHint` | `SchemaViolation` | 告知刷新中,失败时提示检查日志 |

**注**: 状态切换时需同步更新 Flags,确保 Markdown、JSON、日志三者一致。

### 4.4 诊断分类 (`DebugUtil` 类别)

| 类别 | 用途 |
| --- | --- |
| `TextEdit.MatchTrace` | 记录所有匹配位置、选区上下文与快照指纹,用于回放决策 |
| `TextEdit.Persistence` | 覆盖 `_commit`、`_persist_as` 等持久化动作,包含参数快照、耗时与异常 |
| `TextEdit.External` | 捕获文件监听器事件、冲突详情与防抖统计 |
| `TextEdit.Schema` | 记录响应结构失效或解析失败时的原始 Markdown/JSON |

环境变量 `ATELIA_DEBUG_CATEGORIES` 控制输出,调试模式下 Guidance 可提示查看对应日志。

---

## 5. 典型交互流程

### 5.1 单匹配替换 (已实现)
1. LLM 调用 `_replace(old_text="foo", new_text="bar")`
2. Widget 检测到唯一匹配,直接执行替换
3. 返回 `status=Success`,`delta=0`,`new_length=1234`
4. **Immediate 模式**: 保持 `workflow_state=Idle`(待实现)
5. **Manual 模式**: 切换到 `workflow_state=PersistPending`,提示调用 `_commit`(待实现)

### 5.2 多匹配确认 (已实现)
1. LLM 调用 `_replace(old_text="foo", new_text="bar")`
2. Widget 检测到 3 处匹配,生成虚拟选区(最多 5 个)
3. 返回 `status=MultiMatch`,`selection_count=3`,附带候选表格
4. LLM 查看快照中的 `[[SEL#1]]...[[/SEL#1]]` 标记,选择目标选区
5. LLM 调用 `_replace_selection(selection_id=2)`
6. Widget 执行选定替换,返回 `status=Success`

### 5.3 持久化失败 (待实现)
1. Manual 模式下,LLM 调用 `_commit` 尝试写回
2. 底层存储返回失败(如文件被锁定)
3. Widget 返回 `status=PersistFailure`,切换到 `workflow_state=OutOfSync`
4. Guidance 建议先调用 `_diff` 查看差异,再决定是否重试或 `_refresh`

### 5.4 外部冲突 (待实现)
1. Widget 缓存有未提交修改,此时文件监听器检测到外部写入
2. Widget 触发 `status=ExternalConflict`,进入 `workflow_state=OutOfSync`
3. Guidance 建议调用 `_diff` 对比缓存与底层差异
4. LLM 评估后选择 `_refresh`(丢弃缓存)或人工介入

### 5.5 只读模式 (待实现)
1. 配置 `PersistMode=Disabled`
2. 所有写入操作仅更新缓存,Flags 恒含 `PersistReadOnly`
3. Guidance 持续提醒"结果仅存于缓存,不会写回底层"

---

## 6. Markdown 响应规范

### 6.1 结构定义
所有返回正文采用半结构化 Markdown,固定包含四个段落(按顺序):

#### 6.1.1 状态头部 (必填)
三行固定顺序: `status` → `state` → `flags`,字段值置于内联代码。无标志时输出 `flags: -`。

```markdown
status: `Success`
state: `PersistPending`
flags: `PersistPending`, `DiagnosticHint`
```

#### 6.1.2 概览 (必填)
以 `### [OK] 概览` 或 `### [Warning] 概览` 或 `### [Fail] 概览` 开头,列表项键名固定为 `summary`、`guidance`。缺省时写"(留空)"。

```markdown
### [OK] 概览
- summary: 已完成替换,缓存已更新
- guidance: 调用 `_commit` 写回,或 `_discard` 放弃修改
```

#### 6.1.3 指标 (必填)
以 `### [Metrics] 指标` 开头,表头固定为 `指标` / `值`,缺失值用 `-` 占位。

```markdown
### [Metrics] 指标
| 指标 | 值 |
| --- | --- |
| delta | +12 |
| new_length | 1824 |
| selection_count | - |
```

#### 6.1.4 候选选区 (可选)
存在虚拟选区时输出 `### [Target] 候选选区` 表格,列顺序固定:

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
- summary: 在目标文本中检测到 3 处候选
- guidance: 请选择候选编号后调用 `_replace_selection`,或提供更具体的 `old_text`

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

```csharp
// 使用字符串插值与辅助方法保持一致性
var responseMarkdown = $$"""
status: `{{status}}`
state: `{{workflowState}}`
flags: {{FormatFlags(flags)}}

### [{{statusIcon}}] 概览
- summary: {{summaryLine}}
- guidance: {{guidanceLine ?? "(留空)"}}

### [Metrics] 指标
| 指标 | 值 |
| --- | --- |
| delta | {{FormatDelta(metrics.Delta)}} |
| new_length | {{metrics.NewLength}} |
| selection_count | {{FormatSelectionCount(metrics.SelectionCount)}} |
{{candidatesBlock ?? string.Empty}}
""".Trim();

// 辅助方法
static string FormatFlags(TextEditFlag flags)
    => flags == TextEditFlag.None
        ? "-"
        : string.Join(", ", EnumerateFlags(flags).Select(f => $"`{f}`"));

static string FormatDelta(int delta)
    => delta >= 0 ? $"+{delta}" : delta.ToString();
```

## [Cycle] WorkflowState 定义
| WorkflowState | 描述 |
| --- | --- |
| `Idle` | 缓存与底层文本同步，无挂起事务。 |
| `SelectionPending` | `_replace` 产生多处匹配，等待 `_replace_selection` 或 `_discard`。 |
| `PersistPending` | 缓存已修改但尚未提交（Manual 模式或延迟提交）。 |
| `OutOfSync` | 缓存与底层不一致（提交失败或外部写入）。 |
| `Refreshing` | 正在刷新底层快照，临时禁止写入操作。 |

## [Cycle] WorkflowState -> Flags 映射
| WorkflowState | 必含 Flags | 可选 Flags | Guidance 重点 |
| --- | --- | --- | --- |
| `Idle` | `TextEditFlag.None` | `PersistReadOnly`、`DiagnosticHint` | 告知当前可执行范围或只读提示。 |
| `SelectionPending` | `SelectionPending` | `PersistReadOnly`、`DiagnosticHint` | 引导 `_replace_selection` 或 `_discard`。 |
| `PersistPending` | `PersistPending` | `PersistReadOnly`、`DiagnosticHint` | 提醒 `_commit` / `_discard`。 |
| `OutOfSync` | `OutOfSync` | `ExternalConflict`、`DiagnosticHint`、`SchemaViolation` | 禁止 `_commit`，建议 `_diff` + `_refresh`。 |
| `Refreshing` | `DiagnosticHint` | `SchemaViolation` | 告知刷新中，失败时提示检查日志。 |

状态切换时需同步更新 Flags，保持 Markdown、JSON、日志一致。

---

## 7. 工具可见性矩阵 (待实现)

### 7.1 可见性状态说明
- **[Yes]**: 工具可见且可调用
- **[Block]**: 工具隐藏或调用时返回错误

### 7.2 矩阵定义

| WorkflowState | `_replace` | `_replace_selection` | `_append` | `_commit` | `_discard` | `_refresh` | `_diff` | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `Idle` | [Yes] | [Block] | [Yes] | [Yes]* | [Yes] | [Yes] | [Block] | `PersistMode=Immediate` 时 `_commit` 为 [Block];只读模式可隐藏 `_append` |
| `SelectionPending` | [Yes] | [Yes] | [Block] | [Block] | [Yes] | [Yes] | [Yes] | 聚焦候选确认,其余写入工具锁定 |
| `PersistPending` | [Yes] | [Block] | [Yes] | [Yes] | [Yes] | [Yes] | [Yes] | `PersistMode=Disabled` 时隐藏 `_commit` 并输出 `PersistReadOnly` |
| `OutOfSync` | [Yes] | [Block] | [Block] | [Block] | [Yes] | [Yes] | [Yes] | 禁止 `_commit`,优先比对差异 |
| `Refreshing` | [Block] | [Block] | [Block] | [Block] | [Block] | [Block] | [Block] | 刷新完成后按新状态恢复可见性 |

### 7.3 实现建议
- **集中控制**: 实现 `ToolVisibilityMatrix` 类,封装状态→可见工具的映射逻辑
- **单测覆盖**: 针对每个 `WorkflowState`,断言预期的可见工具集合
- **动态调整**: `ITool.Visible` 属性可在运行时切换,响应状态机变化

---

## 8. Guidance 模板与二次动作 (待实现)

### 8.1 按状态生成 Guidance

| WorkflowState | Guidance 模板 |
| --- | --- |
| `SelectionPending` | 调用 `_replace_selection(selection_id=X)` 并传入候选编号,或提供更精确的 `old_text` 重新匹配 |
| `PersistPending` | 调用 `_commit` 写回底层存储,或使用 `_discard` 放弃修改 |
| `OutOfSync` | 建议先调用 `_diff` 查看缓存与底层的差异,再考虑 `_refresh` 或人工处理 |

### 8.2 按标志增强 Guidance

| Flag | 附加提示 |
| --- | --- |
| `PersistReadOnly` | Summary 或 Guidance 中注明"结果仅存于缓存,不会写回底层" |
| `ExternalConflict` | 优先使用 `_diff` 评估差异,避免直接 `_commit` |
| `SchemaViolation` | 输出 `[Fail] 响应格式不符合规范`,并指向 `TextEdit.Schema` 日志 |

---

## 9. 虚拟选区与候选编号 (已实现)

### 9.1 设计原理
- **多匹配缓存**: `_replace` 检测到多处匹配时,生成 `SelectionState` 缓存,包含快照哈希、匹配文本、候选列表
- **唯一编号**: 每个候选分配递增 ID,从 1 开始,在快照中插入 `[[SEL#X]]` / `[[/SEL#X]]` 标记
- **重定位依据**: `Occurrence` 字段记录匹配序号,用于校验文本未变化

### 9.2 确认流程
1. LLM 查看快照中的标记,选择目标候选
2. 调用 `_replace_selection(selection_id=2, new_text="...")`
3. Widget 校验:
   - 快照指纹是否与当前缓存一致
   - 目标位置文本是否仍匹配 `needle`
4. 通过校验后执行替换,清除 `SelectionState`,返回 `Success`

### 9.3 失效条件
- 任意写入操作成功后,旧 `SelectionState` 自动清除
- Guidance 应提示"选区已作废,需重新生成"

---

## 10. 持久化策略 (待实现)

### 10.1 三种模式

| PersistMode | 行为 | 适用场景 |
| --- | --- | --- |
| `Immediate` | 写入成功后立即同步到底层,`workflow_state` 保持 `Idle` | 实时保存场景,如配置文件编辑 |
| `Manual` | 编辑成功后进入 `PersistPending`,需显式调用 `_commit` | 事务性编辑,允许撤销 |
| `Disabled` | 所有写入仅更新缓存,Flags 恒含 `PersistReadOnly` | 只读预览或临时测试 |

### 10.2 失败处理
- **Immediate 失败**: 返回 `status=PersistFailure`,进入 `OutOfSync`,Guidance 建议重试或 `_persist_as`
- **Manual 失败**: 保持 `PersistPending`,提示检查权限或磁盘空间

### 10.3 扩展动作
- `_commit`: 显式提交缓存到底层(Manual 模式专用)
- `_discard`: 丢弃缓存修改,刷新为底层内容
- `_persist_as`: 另存为新路径(扩展功能)

---

## 11. 外部变更监测 (待实现)

### 11.1 监听机制
- 通过文件系统监听器(如 `FileSystemWatcher`)捕获底层文本变动
- 事件防抖: 200ms 内的连续事件聚合为单次处理,避免状态频繁抖动

### 11.2 冲突处理
- **缓存无修改**: 静默刷新快照,在 Summary 中提示"内容已更新"
- **缓存有修改**: 进入 `OutOfSync`,隐藏 `_commit`,显式展示 `_diff` 与 `_refresh`

### 11.3 诊断日志
记录事件来源、变更指纹、时间戳,便于排查误触发或延迟问题。

---

## 12. 状态流转执行合同 (待实现)

| 事件 | 前置 State | PersistMode | 返回 Status | 新 State | Flag 变化 | 工具可见性要点 |
| --- | --- | --- | --- | --- | --- | --- |
| `_replace` 单匹配 | `Idle` | Immediate | `Success` | `Idle` | 保持 `None` | `_replace_selection` 隐藏 |
| `_replace` 单匹配 | `Idle` | Manual | `Success` | `PersistPending` | 添加 `PersistPending` | `_commit` 可见 |
| `_replace` 多匹配 | 任意 | 任意 | `MultiMatch` | `SelectionPending` | 添加 `SelectionPending` | `_replace_selection` 可见,其余写入锁定 |
| `_replace_selection` 成功 | `SelectionPending` | Immediate | `Success` | `Idle` | 移除 `SelectionPending` | 恢复默认可见性 |
| `_replace_selection` 成功 | `SelectionPending` | Manual | `Success` | `PersistPending` | `SelectionPending` → `PersistPending` | `_commit` 可见 |
| `_commit` 成功 | `PersistPending` | Manual | `Success` | `Idle` | 移除 `PersistPending` | 状态归稳 |
| `_commit` 失败 | `PersistPending` | Manual | `PersistFailure` | `OutOfSync` | 移除 `PersistPending`,添加 `OutOfSync` | `_commit` 隐藏,引导 `_diff` + `_refresh` |
| 外部写入检测 | `Idle` / `PersistPending` | 任意 | `ExternalConflict` | `OutOfSync` | 添加 `OutOfSync`、`ExternalConflict` | `_diff` 可见,禁止 `_commit` |
| `_refresh` 启动 | 任意 | 任意 | `Success` / `NoOp` | `Refreshing` | 附加 `DiagnosticHint` | 刷新期间隐藏全部工具 |

**实现建议**: 封装状态机为 Reducer/Controller,集中管理状态转换与副作用,单测覆盖全部路径。

---

## 13. 响应解析与消费 (LLM 工具视角)
- `workflow_state = SelectionPending`：Guidance 固定提示调用 `_replace_selection` 并传入编号 X，或提供更精确的 `old_text`。
- `workflow_state = PersistPending`：提供“调用 `_commit` 写回”与“使用 `_discard` 放弃”的二选一建议。
- `workflow_state = OutOfSync` 或 Flags 含 `ExternalConflict`：提醒先 `_diff` 查看差异，再考虑 `_refresh` 或人工处理。
- Flags 含 `PersistReadOnly`：在 Summary 中附注“结果仅存于缓存，不会写回”。
- Flags 含 `SchemaViolation`：输出“[Fail] 响应格式不符合规范”，并指向 `TextEdit.Schema` 日志。

## [Notebook] Markdown 响应规范
所有返回正文均采用半结构化 Markdown，固定包含四个段落：

1. **状态头部**（必填）：三行顺序固定为 `status` → `state` → `flags`，字段值置于内联代码，无标志时输出 `flags: -`。
   ```markdown
   status: `Success`
   state: `PersistPending`
   flags: `PersistPending`, `DiagnosticHint`
   ```
2. **概览**（必填）：以 `### [OK] 概览` 开头，列表项键名固定为 `summary`、`guidance`；缺省时写“（留空）”。
3. **指标**（必填）：以 `### [Metrics] 指标` 开头，表头固定 `指标` / `值`，缺失值用 `-` 占位。
4. **候选选区**（可选）：存在虚拟选区时输出 `### [Target] 候选选区` 表格，列顺序固定为 `Id`、`MarkerStart`、`MarkerEnd`、`Preview`、`Occurrence`、`ContextStart`、`ContextEnd`。

```markdown
status: `MultiMatch`
state: `SelectionPending`
flags: `SelectionPending`

### [OK] 概览
- summary: [Warning] 在 MemoryNotebook 中检测到 3 处候选。
- guidance: 请选择候选编号后调用 `_replace_selection`，或提供更具体的 `old_text`。

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

推荐 Formatter 结构：

```csharp
var responseMarkdown = $$"""
status: `{{status}}`
state: `{{workflowState}}`
flags: {{FormatFlags(flags)}}

### [OK] 概览
- summary: {{summaryLine}}
- guidance: {{guidanceLine}}

### [Metrics] 指标
| 指标 | 值 |
| --- | --- |
| delta | {{FormatDelta(metrics.Delta)}} |
| new_length | {{metrics.NewLength}} |
| selection_count | {{FormatSelectionCount(metrics.SelectionCount)}} |
{{candidatesBlock is null ? string.Empty : $"\n{candidatesBlock}"}}
""".Trim();
```

## [Serializer] 序列化与渲染策略
- **FormatFlags**：统一使用 `FormatFlags` 将 `TextEditFlag` 枚举渲染为反引号包裹的名称列表；无标志时输出 `-`。
    ```csharp
    private static string FormatFlags(TextEditFlag flags)
            => flags == TextEditFlag.None
                    ? "-"
                    : string.Join(", ", Enumerate(flags).Select(flag => $"`{flag}`"));
    ```
- **Flags 枚举遍历**：`Enumerate` 仅返回非 `None` 的标志，保持顺序稳定，方便测试快照。
- **JSON / 持久化**：可选同时输出整数位掩码与字符串数组，示例：
    ```json
    {
        "flags": {
            "mask": 6,
            "names": ["PersistPending", "DiagnosticHint"]
        }
    }
    ```
- **Debugger 输出**：调试日志与 Markdown 返回使用同一 Formatter，确保状态头部与日志对齐。

## [Fix] 错误处理与调试
- Markdown 结构缺失、标题不匹配或必填字段为空时，Summary 退化为 `[Fail] 响应格式不符合规范`，`workflow_state` 设为 `OutOfSync`（或保持原值），并在 `flags` 中追加 `SchemaViolation`。
- 使用 `DebugUtil.Print("TextEdit.Schema", ...)` 记录原始 Markdown，方便回放。
- 单元测试覆盖结构缺失、表头拼写错误、候选列顺序错误等；建议在 CI 中增加 Markdown AST 校验或 parser 回测。

## [Overlay] 虚拟选区与候选编号
- `_replace` 多匹配时生成 `SelectionState` 缓存，包含 `(snapshot_hash, needle, default_replacement, entries)` 等信息。
- 每个候选条目提供唯一 `Id`，在快照中插入 `[[SEL#X]]` / `[[/SEL#X]]` 标记，`Occurrence` 记录匹配序号以便重定位。
- `_replace_selection` 接收 `selection_id` 与可选 `new_text`，执行前需校验快照指纹与当前缓存一致，且目标文本仍匹配 `needle`。
- 任意写入成功后需清除旧 `SelectionState`，防止复用陈旧选区；Guidance 应提示选区作废需重新生成。

## [Persist] 持久化策略与失败处理
- **Immediate**：写入成功后返回 `workflow_state=Idle`；失败进入 `OutOfSync` 并追加 `OutOfSync` flag，Summary 使用警示语气。
- **Manual**：编辑成功后进入 `PersistPending`，提示 `_commit` / `_discard`；提交失败时保持 `PersistPending` 并给出重试或 `_persist_as` 建议。
- **Disabled**：所有写入仅更新缓存，Flags 恒加 `PersistReadOnly`；Guidance 提醒结果不会写回。
- **扩展动作**：预留 `_persist_as`、`_discard` 等显式调用，状态机需在动作完成后重算 Flags 与工具可见性。

## [External] 外部变更监测
- Watcher 捕获底层文本变动时，如缓存无修改则刷新快照并在 Summary 中提示“内容已更新”。
- 当缓存有修改时进入 `workflow_state=OutOfSync`，隐藏 `_commit` 并显式展示 `_diff`、`_refresh`，Guidance 建议先比对再决定覆盖或刷新。
- 对文件事件进行去抖（如 200 ms）防止频繁状态抖动；调试日志记录事件来源与变更指纹，便于排查。

## [Loop] 状态流转执行合同
| 事件 | 前置 WorkflowState | PersistMode | OperationStatus (`status`) | 新 WorkflowState | Flag 变化 | 工具可见性要点 |
| --- | --- | --- | --- | --- | --- | --- |
| `_replace` 命中 1 处 | `Idle` / `PersistPending` | Immediate | `Success` | `Idle` | 保持 `TextEditFlag.None` | `_replace_selection` 隐藏；Immediate 模式下 `_commit` 隐藏。 |
| `_replace` 命中 1 处 | `Idle` / `PersistPending` | Manual | `Success` | `PersistPending` | 添加 `PersistPending`（保留只读标志） | `_commit` 可见，提示提交/放弃。 |
| `_replace` 多匹配 | 任意 | 任意 | `MultiMatch` | `SelectionPending` | 添加 `SelectionPending` | `_replace_selection` 可见，其余写入工具锁定；`_discard` 可见。 |
| `_replace_selection` 成功 | `SelectionPending` | Immediate | `Success` | `Idle` | 移除 `SelectionPending` | 写入工具恢复默认。 |
| `_replace_selection` 成功 | `SelectionPending` | Manual | `Success` | `PersistPending` | `SelectionPending` → 移除，保留/追加 `PersistPending` | `_commit` 保持可见。 |
| `_commit` 成功 | `PersistPending` | Manual | `Success` | `Idle` | 移除 `PersistPending` | `_commit` 隐藏，状态归稳。 |
| `_commit` 失败 | `PersistPending` | Manual | `PersistFailure` | `OutOfSync` | 移除 `PersistPending`，追加 `OutOfSync`（必要时 `ExternalConflict`） | `_commit` 隐藏，引导 `_diff` + `_refresh`。 |
| Watcher 检测外部写入 | `Idle` / `PersistPending` | 任意 | `ExternalConflict` | `OutOfSync` | 添加 `OutOfSync`，可叠加 `ExternalConflict` | `_diff` 可见，禁止 `_commit`。 |
| `_refresh` 启动 | 任意 | 任意 | `Success` / `NoOp` | `Refreshing`（临时） | 附加 `DiagnosticHint`；失败时叠加 `SchemaViolation` | 刷新期间隐藏全部工具；完成后按新状态重新计算。 |

> 建议实现 reducer/状态控制器，将事件驱动逻辑集中管理，并在测试中串联断言。

## [Search] 消费方解析指南（LLM 工具视角）
1. 解析状态头部的 `status`、`state`、`flags`，并校验组合是否满足合同（例如 `state=PersistPending` 必须包含 `PersistPending` flag）。
2. 将概览段转为键值对，方便多语言呈现与日志记录。
3. 解析指标表为字典，确保 `delta`、`new_length`、`selection_count`（如适用）存在；缺失时记录 `TextEdit.Schema` 诊断。
4. 若存在候选表，逐行解析为结构化数组，保留 `Id`、`ContextStart`、`ContextEnd` 等字段以便 `_replace_selection` 与 UI 定位。
5. 根据 Flags 判断是否需要额外动作（只读、诊断提示等），并与 `workflow_state` 交叉校验。

## [Test] 测试矩阵
- **状态控制器单测**：覆盖全部 `WorkflowState`，断言 Flags、工具可见性与 Markdown 头部一致。
- **集成流程测试**：模拟 Immediate / Manual / Disabled 三种 PersistMode，多匹配、提交失败、外部冲突等路径。
- **Markdown 快照**：维护 Success / MultiMatch / PersistFailure / ExternalConflict / Refreshing 等示例响应，防止模板回退。
- **Schema 验证**：Formatter 增加反向解析或 AST 检查，一旦输出不合规立即置 `SchemaViolation` 并在测试中失败。
- **日志断言**：`DebugUtil` 输出至少包含 `operation_id`、`status=`、`state=`、`flags=`、耗时、异常堆栈，确保诊断可用。

## [Diagnostics] Diagnostics 分层
- **TextEdit.MatchTrace**：记录所有匹配位置、选区上下文与快照指纹，便于回放 `_replace` / `_replace_selection` 的决策。
- **TextEdit.Persistence**：覆盖 `_commit`、`_persist_as` 等持久化动作，包含参数快照、耗时与异常。
- **TextEdit.External**：捕获 Watcher 事件、冲突详情与去抖统计。
- **TextEdit.Schema**：在响应结构失效或解析失败时记录原始 Markdown/JSON。
- 环境变量 `ATELIA_DEBUG_CATEGORIES` 控制输出类别；调试模式下 Guidance 可提示查看对应日志。

## [Health] Diagnostics 与运维
- 关键路径统一调用 `DebugUtil.Print`，按类别区分（`TextEdit.MatchTrace`、`TextEdit.Persistence`、`TextEdit.External`、`TextEdit.Schema`）。
- 日志必须携带 `operation_id`、`status`、`state`、`flags`、耗时与异常信息。
- 提供脚本 tail `.codecortex/ldebug-logs/TextEdit*.log`，方便 Agent 或人工实时查看。
- 在 CI 中校验关键路径日志是否存在，防止调试输出被误删。

## [Plan] 实施路线图
| 阶段 | 目标 | 关键交付 |
| --- | --- | --- |
| 0. 现状对齐 | 建立测试基线 | 补齐 `_replace`、`_replace_selection`、快照渲染的 snapshot / 单测，梳理现状文档。 |
| 1. 响应契约 | 落地 `TextEditResponse` 与 Formatter | 引入枚举/record，适配 `LodToolExecuteResult`，单测断言 Markdown 模板。 |
| 2. 状态机整理 | 内聚状态与工具可见性 | 实现 WorkflowState 控制器，补齐状态切换测试。 |
| 3. 候选强化 | Overlay + selection_id 全链路 | 对齐 `SelectionState` 缓存、Marker 生成、确认流程，覆盖多匹配到成功的集成测试。 |
| 4. 持久化策略 | 支持 `PersistMode` 与 `_commit/_discard` | 封装持久化接口（可先 Stub），模拟失败路径，并在响应中暴露 Flags。 |
| 5. 外部变更 | Watcher + 冲突工具 | 引入监听器、`_refresh`、`_diff`，测试冲突与恢复流程。 |
| 6. 数据源接入 | 可插拔数据源 | 文件、内存、远端 provider，覆盖持久化失败与冲突回放。 |
| 7. 体验打磨 | 文档、提示词、调试工具 | 更新提示词、示例对话，补充 Debug 文档，输出演示脚本。 |

各阶段需保证：编译通过、现有测试全绿、新增测试覆盖关键路径，并同步更新开发笔记。

## [Cleanup] 遗留组件处理策略
- **功能冻结**：`TextEditorWidget` 标记为已弃用，只做缺陷修复。
- **薄适配层**：旧调用方通过 shim 转调新组件，不支援新特性时返回 Guidance 提示迁移。
- **测试守护**：保留旧组件基础回归测试，防止冻结期间回归；新能力不再写入旧测试。
- **迁移清单**：梳理旧接口调用点，制定迁移计划并跟踪覆盖率。
- **移除倒计时**：当 shim 覆盖率 < 10% 时，规划下线时间表与迁移 checklist。

## [Warning] 风险与缓解
| 风险 | 说明 | 缓解措施 |
| --- | --- | --- |
| 状态漂移 | 多线程/多 Agent 导致状态与缓存不一致 | 为 `SelectionState`、`PersistState` 增加指纹校验，测试覆盖空文本、重复 needle、大文档等边界情况。 |
| Watcher 抖动 | 文件事件抖动频繁切换 `OutOfSync` | 对事件去抖（如 200ms 聚合），并在日志中记录细节。 |
| 持久化耗时 | 写入耗时长影响体验 | 在 Diagnostics 中记录耗时，必要时提供后台写入或进度提示。 |
| 提示词错配 | 提示词未同步契约更新 | 每次调整契约后回放多轮对话脚本，验证解析正确性。 |
| 适配层遗漏 | 旧调用点未迁移导致功能缺失 | 维护迁移清单并在 shim 中输出迁移 Guidance。 |

## 18. 后续扩展方向

### 18.1 抽象选区与缓存
- 扩展到多文件或二进制场景
- 支持结构化选区(如 AST 节点)

### 18.2 UI 集成
- 与 LiveContextProto UI overlay 对接
- 展示实时候选和状态栏

### 18.3 历史与撤销
- 引入多版本快照
- 支持撤销/重做与时间线回放

### 18.4 结构化匹配
- 支持 `match_selector` / AST 匹配
- 提升大规模重构定位精度

### 18.5 协同编辑
- 探索多 Agent 协同编辑
- 共享只读快照 + 互斥写入协议

---

## 附录 A: 术语对照表

| 术语 | 说明 |
| --- | --- |
| `LodToolExecuteResult` | 工具返回类型,包含 `Status` (ToolExecutionStatus) 和 `Result` (LevelOfDetailContent) |
| `ToolExecutionStatus` | 工具执行状态枚举: `Success` / `Failed` / `Skipped` |
| `LevelOfDetailContent` | 双级内容容器,包含 `Basic`(简要)和 `Detail`(详细)两级文本 |
| `LevelOfDetail` | 细节等级枚举: `Basic` / `Detail`,用于控制上下文渲染 |
| `MethodToolWrapper` | 通过注解自动生成工具定义的包装器 |
| `ToolExecutor` | 工具分发与执行的协调器,负责调用、监控、日志 |
| `AgentState` | Agent 状态管理器,维护 Recent History 与通知队列 |
| `WorkflowState` | TextEditor2Widget 的状态机枚举(待实现) |
| `TextEditFlag` | 位标志枚举,由 `WorkflowState` 派生(待实现) |
| `SelectionState` | 多匹配时的虚拟选区缓存 |
| `PersistMode` | 持久化模式枚举: `Immediate` / `Manual` / `Disabled`(待实现) |

---

**文档版本**: 2025-战略版-v1.2
**最后更新**: 2025年11月9日
**维护者**: Atelia 开发团队
本战略版文档明确了以 `TextEditor2Widget` 为核心的 LLM 文本编辑蓝图，为后续实现、测试与迁移提供统一依据。
