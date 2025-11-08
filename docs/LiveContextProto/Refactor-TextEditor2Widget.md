# TextEditor2Widget LLM-first 推进计划（2025 战略版）

> 聚焦 `TextEditor2Widget` 作为唯一演进方向，以「LLM 独占缓冲区 + 结构化反馈 + 安全持久化」打造新一代文本编辑能力。

## [Compass] 战略转向概览
- **冻结旧链路**：`TextEditorWidget + TextReplacementEngine` 进入安全冻结状态，仅接受高优先级缺陷修复。
- **新组件承载全部差异化能力**：多匹配候选、持久化策略、外部冲突处理等全部在 `TextEditor2Widget` 内实现，不再回写旧实现。
- **薄适配层过渡**：旧调用方向新组件迁移时通过 shim 转调，逐步削减遗留入口。
- **统一交互协议**：面向 LLM 的响应契约、状态机、诊断日志形成闭环，为接入真实数据源与 UI overlay 做准备。

## [Target] 范围与设计原则
1. **LLM-first 响应**：`summary`、`guidance`、`candidates` 构成最小闭环，明确“发生了什么”“下一步做什么”。
2. **独占可校验缓存**：Widget 缓存是唯一可信源；外部写入需显式确认后同步。
3. **显式状态机**：由 `WorkflowState` + `Flags` 控制工具可见性，避免误操作。
4. **安全持久化**：持久化策略、失败回退、只读提示均内置在组件中。
5. **分层诊断**：业务响应保持骨干信息，调试细节统一写入 `DebugUtil` 分类日志。

## [Agent] Agent 环境背景
- **工具注册与参数推断**：`MethodToolWrapper` 读取 `[Tool]` / `[ToolParam]` 注解，自动生成规范化 `ITool` 定义，并注入格式化占位符（如 `memory_notebook_replace`）。模型无需自行拼写参数元信息。
- **调用分发与监控**：`ToolExecutor` 基于 `ParsedToolCall.ToolCallId` 分发请求，并在 `LodToolCallResult` 中附带 `ToolCallId`、耗时、`ToolExecutionStatus` 等元数据，统一写入调试日志与工具结果消息。
- **上下文渲染策略**：`AgentState.RenderLiveContext` 将 Observation / Tool Result / Action 交替拼接，Detail 仅保留最新观测，其余降为 Basic，以控制 token 体积。所有工具反馈通过 `ToolResultsEntry` 注入历史队列，供下一轮模型读取。
- **对本组件的启示**：`TextEditor2Widget` 专注于编辑相关响应，操作 ID、耗时等基础元信息由框架兜底；只需确保 Markdown 输出与 `TextEditResponse` 同步即可。

## [Puzzle] 核心术语与契约对齐
| 名称 | 说明 |
| --- | --- |
| **OperationStatus (`status`)** | 单次工具调用的即时结果（如 `Success`、`MultiMatch`、`PersistFailure`、`ExternalConflict`），驱动 Summary 语气与候选生成。 |
| **WorkflowState (`workflow_state`)** | Widget 的持久状态（`Idle`、`SelectionPending`、`PersistPending`、`OutOfSync`、`Refreshing`），决定下一步可执行动作与工具可见性。 |
| **Flags (`flags`)** | `[Flags]` 枚举，由 `workflow_state` 推导并叠加只读/诊断等附加语义，例如 `PersistReadOnly`、`ExternalConflict`、`DiagnosticHint`、`SchemaViolation`。 |
| **ToolVisibility** | `workflow_state` 与运行时条件（PersistMode、是否存在候选、是否只读等）共同决定的工具可见性矩阵。 |
| **Diagnostics** | 调试视图统一通过 `DebugUtil` 输出到分类日志（如 `TextEdit.MatchTrace`、`TextEdit.Persistence`、`TextEdit.Schema`），与响应保持同源信息。 |

只有在这些维度保持一致时，LLM 才能既理解响应语义，又安全规划后续动作。

## [Loop] 典型交互流程

1. **单匹配替换**：`_replace` → `status=Success`。Immediate 模式保持 `workflow_state=Idle`；Manual 模式切换到 `PersistPending` 并提醒 `_commit`。
2. **多匹配确认**：`_replace` → `status=MultiMatch`。生成候选并进入 `workflow_state=SelectionPending`，仅保留 `_replace_selection`、`_discard`、`_diff` 等工具。
3. **选区确认**：`_replace_selection` → `status=Success`。Immediate 模式回到 `Idle`；Manual 模式保持 `PersistPending` 等待提交。
4. **持久化失败**：`_commit` → `status=PersistFailure`。切换到 `workflow_state=OutOfSync`，隐藏 `_commit` 并引导 `_diff` + `_refresh`。
5. **外部冲突**：Watcher 触发 `status=ExternalConflict`。进入 `workflow_state=OutOfSync`，Guidance 建议对比差异然后决定是否 `_refresh`。
6. **只读模式提示**：在 `PersistMode=Disabled` 下，写入操作保持当前 `workflow_state`，Flags 持续输出 `PersistReadOnly` 并提醒结果仅存于缓存。

## [Control] TextEditResponse 契约
| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `status` | `TextEditStatus` | 即时结果：`Success` / `NoMatch` / `MultiMatch` / `NoOp` / `PersistFailure` / `ExternalConflict` / `Exception`。 |
| `workflow_state` | `TextEditWorkflowState` | Widget 持久状态：`Idle` / `SelectionPending` / `PersistPending` / `OutOfSync` / `Refreshing`。 |
| `summary` | `string` | 单行结论，建议使用 [OK]/[Warning]/[Fail] 等视觉符号。 |
| `guidance` | `string?` | 下一步建议，可为空。 |
| `metrics` | `TextEditMetrics` | `{ delta: int, new_length: int, selection_count?: int }`；仅在存在虚拟选区时返回 `selection_count`。 |
| `candidates` | `IReadOnlyList<TextEditCandidate>?` | 多匹配候选集合，单匹配时为 `null` 或空数组。 |
| `flags` | `TextEditFlag` | `[Flags]` 枚举，由状态派生；无标志时为 `TextEditFlag.None`。 |

```csharp
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

public enum TextEditWorkflowState {
    Idle,
    SelectionPending,
    PersistPending,
    OutOfSync,
    Refreshing,
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
    DiagnosticHint   = 1 << 6,
}
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

## [Matrix] 工具可见性矩阵（示意）
| WorkflowState | `_replace` | `_replace_selection` | `_append` | `_commit` | `_discard` | `_refresh` | `_diff` | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `Idle` | [Yes] | [Block] | [Yes] | [Yes]* | [Yes] | [Yes] | [Block] | `PersistMode=Immediate` 时 `_commit` 设为 [Block]；只读模式可隐藏 `_append`。 |
| `SelectionPending` | [Yes] | [Yes] | [Block] | [Block] | [Yes] | [Yes] | [Yes] | 聚焦候选确认，其余写入工具锁定。 |
| `PersistPending` | [Yes] | [Block] | [Yes] | [Yes] | [Yes] | [Yes] | [Yes] | `PersistMode=Disabled` 时隐藏 `_commit` 并输出 `PersistReadOnly`。 |
| `OutOfSync` | [Yes] | [Block] | [Block] | [Block] | [Yes] | [Yes] | [Yes] | 禁止 `_commit`，优先比对差异。 |
| `Refreshing` | [Block] | [Block] | [Block] | [Block] | [Block] | [Block] | [Block] | 刷新完成后按最新状态恢复可见性。 |

> 建议实现集中控制器（如 `ToolVisibilityMatrix`），并用单测覆盖“WorkflowState → 可见工具”与“WorkflowState → Flags”。

## [Guide] Guidance 模板与二次动作
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

## [Scope] 后续扩展
- 抽象选区与缓存机制，扩展到多文件或二进制场景。
- 与 LiveContextProto UI overlay 对接，展示实时候选和状态栏。
- 引入多版本快照、撤销/重做与时间线回放能力。
- 支持结构化 `match_selector` / AST 匹配，提升大规模重构定位精度。
- 探索多 Agent 协同编辑（共享只读快照 + 互斥写入协议）。

---
本战略版文档明确了以 `TextEditor2Widget` 为核心的 LLM 文本编辑蓝图，为后续实现、测试与迁移提供统一依据。
