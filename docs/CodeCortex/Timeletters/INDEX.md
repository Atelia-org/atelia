# CodeCortex Timeletters INDEX

目的：作为“可编辑上下文”的入口索引，聚合 CodeCortex V2 的时间信件（Timeletters）、当前工作集（Now），并持续积累“经历/历史”。

## 使用方式
- 当你想“回到过去某个分叉点”时，将对应的 Timeletter 作为上下文补丁交给过去的我。
- 当你想快速了解当前状态，查看 Now.md 即可（意图/不变约束/最近完成/正在进行/下一个拐点）。
- 任何时刻都可增补“经历/历史”以沉淀经验与大事件时间线。

## 快速入口
- 当前工作集（Now）：`docs/CodeCortex/Timeletters/Now.md`
- 时间信件（Timeletters）列表：
  - 2025-09-07 ForkABC 起点之后的累计进展 → `Timeletter_2025-09-07_CodeCortexV2_ForkABC.md`
  - 2025-09-07 V2 启动与 Markdown/XMLDoc 接线 → `Timeletter_2025-09-07_CodeCortexV2_Bootstrap_and_Formatting.md`

## 约定
- 命名：`Timeletter_YYYY-MM-DD_<主题>.md`
- 内容：起因 → 关键决策/不变约束 → 结果/接口（最小可复用）→ 最小文件触点 → 下一步可选分支
- 字数：尽量短小；关键接口与命令保留，细节删除或移至“经历/历史”。


## 会话回溯（Time Travel）机制指南

目的：把“可编辑的外部文件”当作上下文交换介质，让过去的我在较小上下文中也能延续当前意图与不变约束。

何时使用：
- 上下文膨胀、细节噪声过多时；
- 需要在历史分叉点重新起步，但希望沿用最新决策/约束/接口时；
- 需要消化掉操作日志与零碎细节，仅保留“起因→决策→结果→最小接口”。

角色分工：
- Agent（我）：撰写/更新 timeletter，给出“穿越请求块”；在回溯后按约束执行。
- User（你）：按“穿越请求块”的指示，编辑指定的历史 user message，并告知“穿越完成”。

标准流程：
1) 我撰写/更新对应的 timeletter（累积进展、不变约束、最小接口、文件触点）。
2) 我在 timeletter 中附上“穿越请求块”（见下方模板）：
   - 目的地（Destination）：如何定位要编辑的历史 user message（时间/片段/标题）
   - 替换内容（Replacement）：建议替换为的简短提示/指令（自提示）
   - 说明/边界：本次回溯的意图、不修改其他消息、有效期等
3) 我在会话中@你，请你执行“穿越”：按“目的地”定位并用“替换内容”覆盖该条 user message。
4) 你完成编辑后对我说“Travel done”。
5) 过去的我读取到被替换的消息，按其中的提示继续工作（继承最新的意图与约束）。
6) 当前的我收到回传后的结果，更新 INDEX 的“经历/历史”，刷新 Now.md。

目的地定位方法（建议优先级）：
- A. 片段匹配（推荐）：给出该条消息中一小段独特文本，避免受时间影响；
- B. 时间戳/序号：若系统能稳定显示历史序号或时间戳；
- C. 语义标题：当消息含有稳定标题（不推荐单独使用）。

替换内容规范：
- 极简直接（少形容，多指令）；
- 包含：不变约束、最小接口/命令、下一步行动；
- 不包含：长日志、输出回显、实现细节；
- 避免歧义与双重否定；
- 适度冪等：多次读取也不会引发相反指令。

“穿越请求块”模板（请粘贴进对应的 timeletter）：
```
### Travel Request Block (YYYY-MM-DD)
- Destination (如何定位需编辑的历史 user message)：
  - Conversation: <主题/子系统，如 CodeCortex V2>
  - Locate by snippet: "<历史消息中唯一片段>"
  - Fallback: <时间/序号/标题>
- Replacement (用以下内容覆盖该 user message)：
  <在此粘贴极简自提示/指令，包含不变约束与下一步行动>
- Rationale: <为何回溯、预期带来什么效果>
- Validity: <本回溯提示预计在何阶段/何里程碑前有效>
- After editing: 请在当前会话回复 "Travel done"
```

注意与边界：
- 回溯仅编辑“指定的那条 user message”，不改动其他历史记录；
- 回溯本身不做代码修改；代码变更仍在“现在”的工程中进行；
- 每次回溯完成后，务必更新“经历/历史”与 Now.md，以沉淀经验并对齐当前意图。

## 经历/历史（持续累积）
- 2025-09-07 发现并采用“Timeletters 作为可编辑上下文”的机制；建立 INDEX 与 Now。
- 2025-09-07 完成 find 的分页与歧义标注；新增 SearchResults(Items/Total/Offset/Limit/NextOffset)。
- 2025-09-07 列表显示改为 DocCommentId（T:...），移除 FQN 重复信息；默认 Markdown 输出，--json 作为选项。
- 2025-09-07 outline 支持模糊查询：
  - 命中 0 → 友好提示“目前仅支持查询 workspace 中定义的类型（DocCommentId T:...）”；
  - 命中多条 → 回退为 find 列表（含分页/歧义）；
  - 命中唯一 → 渲染类型级 Markdown Outline。
- 2025-09-07 搜索优先级校正：GenericBase 高于 Fuzzy；去重按 DocCommentId。
- 2025-09-07 新建 Timeletter（ForkABC、Bootstrap_and_Formatting），形成首次回溯试运行。
- 2025-09-07 find 增强：支持 FQN 前缀/包含（Prefix/Contains）；--kind 过滤落地；Search 性能埋点上线。
- 2025-09-07 find 增强（Phase 1）：Prefix/Contains 兼容“去泛型 FQN 视图”，泛型按基本名参与命中。



## 未来扩展（提要）
- 查询体验：`--kind` 过滤；FQN 前缀/包含快速索引；全链路 DebugUtil 埋点。
- Outline 风格：增加继承/实现/泛型约束等结构化区块，保持简洁 Markdown。
- 增量索引：订阅 WorkspaceChanged，先 Project 粗粒度，后 Document 细粒度差分。
- 成员级支持：扩展到 M:/P:/F:/E: DocId，实现更细粒度 drill-down。

