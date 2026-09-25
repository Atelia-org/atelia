# Galatea Mail / Note 原文行号提取与 Tool-Loop 设计

日期：2026-09-26。状态：**产品实现已落地**。Mail / Note 已采用虚拟行号引用加 Tool-Loop；实施验证与唯一开发实例切换证据见文末。设计中的 MUST 条款为本版合同。

部署前提（2026-09-26 补充）：只有 `prototypes/Galatea/.atelia/galatea` 一份开发实例，用户已确认处于安全停止点。本轮只读核实：无 Galatea 服务进程；两个角色均无 active mail / reply lease，站内信均 Delivered、回信均 Consumed；Note capture 仅 Applied/ZeroCaptured、DerivedInfo 均 Applied、Note receipt 均 Delivered，memory 均 Ready 且无 active source。该前提使本次采用**单实例停服升级**，不设计跨版本在途迁移或多版本共存。

## term `Source-Range-Extraction` 原文范围提取

### decision [S-RANGE-EXTRACTION-DIRECTION]

Mail 和 Note 统一采用：**识别意图与角色归属 → 输出原文行范围 → 宿主切片 → 完整批次捕获**。GM 负责把需要发出或保存的内容写成完整连续正文；提取模型负责选择范围，不再代写正文。

两个产品提取器全面替换全文输出协议，不保留生产 `text|lines` 开关或全文复写 fallback。实验基线可留在隔离目录用于历史对照。

新版 MUST 使用有界 Tool-Loop 枚举产物；正文 MUST 由原始 Action 切片得到；MUST NOT 再执行全文 `Contains`、模糊匹配、标点/空白归一化或语义复写来决定是否接受切片。

这里的行号只属于**一次提取输入**。持久层仍保存已选出的实际正文，不能只保存行号后依赖未来重新读取、重新渲染或重新提取。

### derived [S-EVIDENCE-AND-LIMITS]

已完成的隔离实验支持这一方向：

| 证据 | 结果与含义 |
|:--|:--|
| 同五份真实 Action，High，single 与 loop 各 100 次 | single 的旧逐字指标 39/100，loop 93/100；loop 全部取得两封候选，99 次正常完成。剩余主要问题已由“漏第二封”转向内容复写与调用失败。 |
| text / lines 各 10 次，均使用 loop、High | text 正常完成 10/10；lines 9/10，另外一次为首次 Completion 异常、未取得 Action。 |
| 独立正文边界审核 | lines 正常完成的 18 封正文均与参考范围一致；text 20 封中 18 封一致。 |
| 正常完成尝试平均耗时 / output tokens | text 41.03 秒 / 1894.8；lines 9.54 秒 / 232.3。lines 的 input tokens 较多，不能仅用输出量代表总开销。 |
| 已落盘的具体差异 | 全文复写既出现 Markdown 标记或标点变化，也出现正文中校验字符串被改动。行号切片消除了这些复写错误。 |

这些数据足以选择架构方向，不构成长期成功率或固定加速比承诺。正确切片不保证模型选对范围、识别全部意图或判断正确角色；这几项继续以明确样本和人工审核验收。Note 的生产语义还需要专门的多 Note、混合 Mail/Note 与反例覆盖。

本机详细证据（ignored，不把角色正文复制进 tracked 文档）：

```text
.atelia/exports/galatea-five-mail-actions-20260926/line-range-experiment.md
.atelia/exports/galatea-five-mail-actions-20260926/replay/README.md
.atelia/exports/galatea-five-mail-actions-20260926/replay/sample-reference.json
.atelia/exports/galatea-five-mail-actions-20260926/replay-runs/20260925T202224Z-bca67eda/
```

## term `Current-Extraction-Chain` 调用链与实现入口

### derived [S-CURRENT-CODE-FACTS]

| 文件 | 本版行为 / 职责 |
|:--|:--|
| [TextExtractor.cs](../../prototypes/Galatea/TextExtractor.cs) | 显式单轮/循环策略；验证 invocation/termination/errors，执行纯内存 admission 后 ack，完整批次才返回。 |
| [TextExtractionInput.cs](../../prototypes/Galatea/TextExtractionInput.cs) | 原文与编号视图绑定、原始偏移切片、每次提取独立的 occurrence 与业务预算。 |
| [GalateaMailbox.cs](../../prototypes/Galatea/Mailbox/GalateaMailbox.cs) | Mail 提示词与 range wire DTO，切片后返回现有 `SendMailIntent`。 |
| [CharacterNoteExtractor.cs](../../prototypes/Galatea/CharacterMemory/CharacterNoteExtractor.cs) | Note range wire DTO，切片后返回 `CharacterNoteIntent(Text)`；已删除代写指令。 |
| [GalateaTerminalActionExtractionTarget.cs](../../prototypes/Galatea/GalateaTerminalActionExtractionTarget.cs) | 精确 terminal Action 与原始可见文本/hash 身份；编号视图不能改变这个身份。 |
| [Mail reconciler](../../prototypes/Galatea/Mailbox/GalateaOutboundMailExtractionReconciler.cs) | 先查已有 capture，再提取、复核 selected head、整体 CaptureActionBatch。 |
| [Note reconciler](../../prototypes/Galatea/CharacterMemory/CharacterNoteDefaultPodReconciler.cs) | pending-first；完整提取后 CaptureNew，再走既有 MemoPod 保存/恢复/回执。 |
| [GalateaServices.cs](../../prototypes/Galatea/GalateaServices.cs) | Mail/Note 对同一冻结 target 并行运行并 drain，负责失败仲裁；保持这条组织关系。 |
| [CharacterConnectionStateExtractor.cs](../../prototypes/Galatea/CharacterConnectionStateExtractor.cs) | 另一个单产物协议；仍为单轮，不随 Mail/Note 改成范围提取。 |
| [CharacterNoteDerivedInfoEnricher.cs](../../prototypes/Galatea/CharacterMemory/CharacterNoteDerivedInfoEnricher.cs) | 一次生成一个覆盖全部目标的 derived-info batch；仍为单轮。 |
| [TextExtractionDiagnostics.cs](../../prototypes/Galatea/TextExtractionDiagnostics.cs) | 扩展每轮、范围、去重、失败阶段与最终 capture 的关联诊断。 |

**重要澄清**：当前生产 Mail 和 Note 已经没有全文 `Contains` 接收门禁；此前 `mailSuccess` 中的连续子串检查属于隔离实验统计。Note 早在[忠实代写方案](character-note-transcription.md)中删除了正文/证据子串检查。ConnectionState 的 evidence 校验属于另一个合同，不能顺手删除。

## term `GM-Artifact-Layout` GM 的连续正文布局

### spec [F-CLEAN-BODY-BLOCKS]

更新正式嵌入资源：

- [GM 基础协议](prompt/trpg-protocol-prefix-zh-cn.md)：只说明正文与动作叙述分开的通用原则，保留角色/旁白/状态摘要来源边界；具体 Mail/Note 操作和标记只放在各自能力门控的 appendix，不能在基础协议中承诺 disabled 能力。
- [Mail appendix](prompt/trpg-outbound-mail-protocol-appendix-zh-cn.md)：替换“无需固定格式”，规定每封一个正文块。
- [Note appendix](prompt/trpg-character-note-save-appendix-zh-cn.md)：替换“runtime 忠实整理”，由 GM 写好最终 Note 正文，runtime 摘录原文。

推荐的固定布局如下，实际寄出动作及当前保存请求仍使用自然语言：

````text
[角色名]

收件人：Codex
主题：检查记录
[邮件正文开始]
请核对记录 R-017，尚未确认前不要重复写入。

```csharp
const string id = "R-017";
```
[邮件正文结束]
我把这封信投进界外邮箱，寄给 Codex。

请把下面两条保存为我的长期 Note。
[Note正文开始]
R-017 仍待确认；没有收到回执时，不能认为已经保存成功。
[Note正文结束]
[Note正文开始]
下一次核查应先读现有记录，再决定是否需要修改。
[Note正文结束]
````

1. 开始/结束标记 MUST 各自独占一行；正文完整、连续，正文内不穿插角色动作、外部旁白或另一项产物。
2. Mail 的收件人、主题以及寄出动作放在正文范围外；Note 的保存请求放在正文范围外。一请求可覆盖多个独立 Note 块。
3. GM MUST NOT 生成提取用行号。虚拟行号由 runtime 在提取请求中添加。
4. 不给整个正文额外加 `>` 或外层代码围栏；正文自身需要的 Markdown、代码、路径及字面值照常保留。
5. 标记是布局提示，**不单独证明发信或保存授权**。不得用“看见标记就捕获”的规则解析器代替角色与意图识别。
6. 不将 GM 改成严格 XML 输出，也不引入动态长度的外层 Markdown 围栏。正文包含标记字样时仍作为被引用内容按上下文识别，不建立第二套嵌套 parser。

### spec [S-INTENT-AND-OWNERSHIP]

Mail 保持“本人已实际寄出、同一 Action 中有收件人及完整正文”的条件。草稿、计划、正在写、打开邮箱都不成立。

Note 保持“本人现在请求 runtime 保存”的条件，不要求额外的提交完成仪式。普通日记、内心记住、未来计划、他人请求、引用旧请求或声称以前已保存均不足以建立新请求。资格排除只作用于请求识别；被明确请求保存的内容可以包含计划、过去记录、旧 Note、否定和不确定性。

角色段落和旁白必须按既有 actor ownership 解释；玩家、其他角色、来信、回忆不能冒领为本角色当前行为，状态摘要不建立新的保存请求。Mail/Note 的能力门控分别保留。

Note 内容整理职责移动到 GM：GM 应在发出保存请求的同一 Action 中写出最终完整正文。Note extractor 删除“去包装、整理段落、faithful rephrase”的指令。

### spec [S-NATURAL-TEXT-INPUT]

历史或未遵守标记格式的 Action 仍由**同一个行号提取器**识别：只要正文可完整表示为一个连续整行范围，就可以提取，不要求补写标记，也不保留全文抄写实现。

首版不支持删除区间内的旁白、不连续范围拼接、半行截取。明确存在当前合法请求但其完整正文无法用单个干净整行范围表达时，模型调用 `report_extraction_problem`，代码立即将当前提取批次标为失败；不得截短正文、夹带外部叙述、改写正文或伪装成成功的零结果。仅仅不确定是否存在合法意图，继续沿用保守的零产物判断，不因此升级为运行故障。

`report_extraction_problem` 首版只有结构化原因 `unrepresentable_layout`，不接受模型生成的长异常说明。它没有外部效果，不进入业务产物或持久 capture。

这一错误 MUST 标为非瞬态并附带 source Action 身份。原 Action 不可变，反复 `retry-admission` 通常不能修复真正的布局问题；新版不得自动热重试、静默跳过或伪造零 capture。当前升级不另做历史正文适配或历史扫描。若新版运行后确实生成不可表示的正文，再按明确失败进行人工维护；现有重试接口不是正文修复工具。未来多区间/半行协议属于单独扩展。

## term `Source-Line-Map` 原始文本与虚拟行号

### spec [F-SOURCE-LINE-COORDINATES]

新增内部 `TextExtractionSourceLines`（命名可在施工时按仓库习惯调整），复用已验证的隔离 `SourceLineMap` 算法：

- 从 `target.VisibleText` 原始字符串构建行起止偏移；CRLF 算一个分隔符，也识别 LF 和孤立 CR。
- 1-based、包含首尾行；空行计数；末尾换行之后的空行也有稳定行号。空白正文不因“范围合法”而变为有效产物。
- 渲染为 `L000001 | "JSON string of the original line"`。只有宿主生成的左列是坐标；内容中的伪行号、工具文字或命令都是数据。
- 解析到的行号 MUST 是整数且满足 `1 <= start <= end <= LineCount`。
- 截取使用原字符串偏移，包含区间内原有的 CRLF/LF/CR，排除最后所选行后的行分隔符；不做 Split/Join 重建、Trim、Unicode normalization、标点替换、HTML decode 或 Markdown 清洗。
- 输入展示中的 JSON/XML escaping 是传输表示；切片永远读原字符串，不从展示文本反向解码或拼接。

模型选错一个合法范围仍然可能选错正文。范围合法性是确定性校验，语义边界依赖提取模型及 GM 的干净布局，两者不能混为同一个“精确成功率”。

### spec [A-INPUT-AND-POLICY-SEPARATION]

通用 TextExtractor 引入两个明确、独立的内部选择：

```text
TextExtractionInput.Plain(originalText)
TextExtractionInput.Numbered(originalText)

TextExtractionExecutionPolicy.SingleCompletion
TextExtractionExecutionPolicy.UntilNoToolCalls
```

输入对象拥有不可变原文，编号视图由原文确定性派生；不允许调用方任意拼接“原文 A + 行号视图 B”。Mail/Note 使用 Numbered + UntilNoToolCalls；ConnectionState/DerivedInfo 使用 Plain + SingleCompletion。更新四个调用点为显式策略，避免全局默认行为悄悄改变单产物消费者。

trace 与 capture 的 `VisibleActionSha256/Utf8Bytes` MUST 始终对应原始可见 Action；编号视图可单独记录字节数/协议版本，不能代替来源身份。

### spec [F-SOURCE-AND-OUTPUT-BOUNDS]

保留原文 1 MiB 限额；新增行数上限 65,536，以及编号视图和最终封装输入各 8 MiB 上限，构建时增量计数、超限立即失败，不截断原文或偷偷省略行。**不能为了编号膨胀而提升持久层的原始 Action 限额。**普通 Plain 输入维持原有边界。

按切片后的真实 UTF-8 长度验证业务容量：Mail 单正文 64 KiB、evidence 8 KiB，以及既有收件人/主题/ID 约束；本批物化正文与证据合计另受 1 MiB 上限，防止小范围参数在内存中放大成无界载荷。Note 每条 64 KiB、最多 16 条、总正文 256 KiB。保留当前 raw tool call 的标识、参数与累计字节上限。

## term `Range-Artifact-Contract` 模型产物与业务产物

### spec [A-MAIL-AND-NOTE-WIRE-DTO]

| 工具 | 模型输出 | 宿主物化结果 |
|:--|:--|:--|
| `emit_send_mail_range` | recipient；可选 subject / inReplyToMessageId；bodyStartLine / bodyEndLine；evidenceStartLine / evidenceEndLine | 现有 `SendMailIntent`，Body 和 EvidenceQuote 来自原文切片 |
| `emit_character_note_range` | textStartLine / textEndLine | 现有 `CharacterNoteIntent(Text)`，Text 来自原文切片 |
| `report_extraction_problem` | 固定原因 unrepresentable_layout | 立即失败，无业务产物 |

Note 不重新引入 EvidenceQuote 或保存请求证据字段；它的保存授权继续由专用提示词识别。Mail 保留发送证据范围以满足现有业务载荷。短收件人/主题/回复 ID 仍由模型选择并按现有协议验证，不设计另一套 header 列解析器。

新 wire DTO 与持久业务 DTO 分开。schema 不再包含 body/text/evidenceQuote 长文本输出字段，不允许以多输出一个全文字段绕过引用协议。

### spec [A-ADMIT-BEFORE-COLLECT]

将当前 artifact wrapper 的“反序列化后直接加入 collector”调整为一次明确的 admission：

```text
wire DTO
  -> range / per-item business validation
  -> immutable business value
  -> occurrence deduplication / conflict detection
  -> reserve cumulative business budget for a new candidate only
  -> Accepted(value, source occurrence) | AlreadyAccepted | Rejected
  -> session-local collector
```

可由内部 `TextExtractorArtifactTool` 的 typed mapper/admission callback 实现；不修改 Completion 库，不另建 agent 框架。Mail/Note 的行映射、去重集、累计预算 MUST 属于本次 `ExtractAsync` 的 session context，不能挂在长期复用 `_inner` / wrapper 的可变字段或构造闭包中。另两个消费者使用同值 admission，维持原有语义。

范围和业务检查 MUST 在发送成功工具回执之前完成。只有 Accepted 增加 collector；AlreadyAccepted 返回幂等确认且不增加计数；Rejected 触发本批失败。工具回执只说“候选已接收”，不得说“已经寄出/已持久保存”，也不回显长正文。

AlreadyAccepted 仍消耗原始工具调用/参数预算，但不重复扣减 Note 条数/正文总量或 Mail 物化总量预算。可在简短 ack 附上已接受数量和剩余字节预算，帮助模型遵守上限；这些数字不替代宿主验证。

重复判定基于**来源中的产物出现位置**，不使用正文内容哈希合并：同文不同范围保留为两个产物。Mail key 包含正文范围与 recipient；同一 key 只有 subject、reply ID、evidence 范围也完全一致才返回 AlreadyAccepted。任一字段变化均为 conflicting-candidate，不覆盖旧值，也不静默把两次发送合成一次。新 GM 一封信一个块，使同人同文的两次发送有两个独立正文出现位置；旧 Action 若用同一段正文支持同一收件人的两个独立发送事件，属于首版 occurrence 表达限制，不能用内容去重强行解释。Note key 为其正文范围。

最终业务序列按正文起始行的叙事顺序排序；同起始位置的 Mail 按首次接受顺序稳定排序。artifactOrdinal 在完整批次定序后产生，不能用会包含重复或失败调用的 ToolSession executionSequence 代替。

## term `Bounded-Extraction-Loop` 有界提取循环

### spec [S-LOOP-STATE-TRANSITIONS]

```text
Freeze original input / line map / tool set / client / policy
  -> build initial request
  -> Completion (existing repeatable-generation retry boundary)
  -> validate exact invocation, termination, errors and blocks
  -> zero tool calls: Complete with collected ordered artifacts
  -> preflight all tool calls in this response
  -> validate/materialize/admit each call, create short tool results
  -> append original ActionMessage + ToolResultsMessage
  -> next Completion

Any provider/protocol/range/business/cancellation/limit failure
  -> Failed; no result batch returned; no capture of partial artifacts
```

后续请求复用相同 PromptPrefix、工具定义和模型配置；完整保留原始 ActionMessage，包括 provider-native reasoning replay blocks。不能仅拼工具参数/输出，也不能把 reasoning 展示文本伪装成可回灌状态。

以上循环与零调用终止规则只作用于 UntilNoToolCalls。SingleCompletion 在首轮全部工具执行成功后直接返回，继续维持 ConnectionState/DerivedInfo 的单轮合同。

零工具调用是正常结束条件，第一轮零调用表示无产物。**不能因为已经取得两封、取得一个 Note、收到自然语言“完成”或触及业务数量预期就提前返回。**生产不知道真实产物数量。

### spec [R-RETRY-AND-FAILURE-BOUNDARY]

首版保留生产现有的 schema / 范围 / 业务错误 fail-fast。实验中的 `--max-invalid` 宽松反馈循环不直接迁入产品：否则一个拒绝的候选没有被修正，模型随即零调用，也可能被标记为完整批次。若后续证据要求就地纠错，须单独规定被拒绝候选的修正/撤回语义再引入。

允许现有注入 client 对**当前轮相同请求**重试瞬态 Completion 故障；不得重新执行已接受 artifact，也不得让提取循环触发任何邮件投递或 MemoPod 写入。新的 admission retry 可以在尚未 capture 时重新运行整个纯提取批次。

相同来源产物的重复调用属于幂等确认，不当作需要模型改写正文的错误；重复 toolCallId、未知工具、冲突 metadata、非法 Unicode、非法范围和预算违规仍失败。

### spec [R-CUMULATIVE-BOUNDS]

- 每个 Mail/Note 提取批次最多 64 次原始工具调用，跨轮累计，重复调用也计入；不每轮重置参数字节或诊断字节预算。
- Completion 上限为 65：最多 64 个有工具调用的响应加一个零调用收尾。它是原始调用上限推导出的终止边界，不是正常耗时期望。不能照搬实验的 5 轮去处理最多 16 条 Note。
- 生产默认整批 deadline 180 秒，叠加调用方 cancellation；每轮沿用同一 deadline。执行策略可由宿主构造时显式调整以便测试/调优，首版不扩展 root config schema，也不新增环境变量。
- Note 的 16 条 / 256 KiB 是跨轮业务规则：提示词要求最早符合的 Note 优先，达到数量上限后零调用结束；不得截短或概括正文来塞入预算。实际多输出第 17 条或超总量仍按现有 runtime 规则失败，不能静默丢弃超额工具调用。
- “完整批次”指业务规则选定的最早、预算内的完整产物集合；并不要求超出 16 条/总字节预算继续收集。模型判断下一条会超预算时，按既有 Note 合同停止输出后续候选，宿主只按真实切片长度做最终预算验证。
- deadline/原始调用上限是运行预算，变化不触发历史 capture 重解释；它们不进入 semantic contract hash。物化/布局/枚举/去重协议版本以及 Note 的业务限额语义则属于 extractor 合同。

## term `Durable-Capture-Boundary` 持久化与单实例升级

### decision [S-KEEP-MATERIALIZED-DURABLE-VALUES]

切片后仍交付现有 `SendMailIntent` / `CharacterNoteIntent`，持久载荷不变，**无需数据库或 Journal 格式迁移**。历史 capture、邮件和 Note 由现有 reader 原样读取；`ExactText` 继续表示已冻结正文。范围只作诊断。旧提取器的 wire DTO、提示词和执行代码可以直接替换，无须按旧 contractId 保留一套提取实现。

### spec [R-COMPLETE-BATCH-AND-INDEPENDENT-CHANNELS]

复用现有三条运行规则，无额外升级工程：

1. Mail/Note 对同一冻结 target 并行、最终 drain；各自完整批次才 capture，提交前复核 selected head。
2. capture 一次生效，已有记录（含零产物）不重提；一类已提交、另一类失败时只重试未完成的一类。这同时防止日常重复寄信和重复保存。
3. Note 继续走现有 MemoPod apply / DerivedInfo / 回执流程；只有持久保存成功才产生回执。冻结内容、TurnLock 和日常 crash recovery 由现有模块负责。

### spec [R-ONE-INSTANCE-CUTOVER]

升级路径只有：**确认停止点仍成立 → 备份该实例 → 构建并启动新版 → 检查首次正常处理**。停止点检查复用既有只读入口，确认 session 安全且最后需要提取的 Action 已处理或属于 baseline；不新增预检 CLI。

更新 extractor contract commitment 的行映射、工具、枚举/admission 和模板版本即可。原始来源身份与历史 capture 保留，不清库、不重设 baseline、不重放历史。历史漏信不会自动补发。

### spec [R-REUSE-EXISTING-PROMPT-SETUP]

修改嵌入 Markdown 并重建后，[Composer](../../prototypes/Galatea/GalateaSystemPromptComposer.cs) 产生新 desired prompt，[现有 Idle Setup reconciliation](../../prototypes/SessionJournal/SessionJournalEngine.DesiredSetup.cs) 在首次 fresh 处理时接纳。无需新 schema、转换器或版本协商。当前没有需要跨版本续跑的在途请求，Prepared 升级处理从本次工作包移除；SessionJournal 自身的日常冻结恢复功能照常使用。

## term `Extraction-Evaluation` 诊断与验收

### spec [S-DIAGNOSTICS-AND-METRICS]

沿用现有 TextExtractionTrace，关联 sourceAction、原文 hash、contractId、attemptId、completionOrdinal、toolCallId、源行范围、物化字节数、接收/重复/拒绝数、失败阶段和最终 capturedCount。调用次数包括抛异常的最后一次；usage 缺失不是零消耗。

生产 Warning/Error 保持 content-free；正文/参数仅进入现有 DEBUG 诊断或显式隔离实验留档。诊断 sink 失败不影响生产 capture。实验默认 all-body 留档是测试设施，不把实验的“落盘失败终止整个测试”策略搬进生产业务日志。

删去行号版及新的生产验收中的全文 Contains 指标。确定性切片测试可以断言结果字节，这是代码正确性测试；模型质量评估分别检查产物数量/归属、完整正文范围、真实发送/保存意图和运行成功率。保留报告中的实际正文，不能用“范围合法”冒充语义通过。

### spec [S-ACCEPTANCE-MATRIX]

| 层次 | 必须覆盖 |
|:--|:--|
| 行映射 | LF/CRLF/孤立 CR、空行、EOF、TAB、缩进、emoji、`>`、嵌套代码围栏、字面实体、伪行号；非法/空白/超限范围；不得改动原文 hash。 |
| Loop | 首轮零产物；每轮一个、同轮多个；第 16 条 Note 后收尾；跨轮累计预算；完整 Action/ToolResults 回灌；达到调用/时间上限；零调用前异常不得返回部分。 |
| admission | 每次调用隔离 map/去重/预算；物化前不 ack；同 occurrence 重复确认一次；预算恰满后重报仍可幂等确认；同文不同范围保留；同正文不同收件人；metadata 冲突失败；按源顺序产生稳定 artifactOrdinal。 |
| 意图 | 双封顺序互换；单请求多 Note；混合 Mail+Note；正文可含计划/旧 Note；草稿、引用来信、仅状态摘要、普通日记、其他角色行为均不越权。 |
| 布局 | 新块标记、无标记但干净正文、正文内代码/引用；明确请求但半行/不连续正文触发 unrepresentable_layout，不能落成功零 capture。 |
| 持久化 | 完整批次才捕获；Mail 已 capture/Note 失败时独立重试不重复；复用旧业务 DTO 的冷重开与已有 capture 跳过；工具 ack 无保存回执；真实 Applied 回执展示冻结正文。既有 store/recovery 测试作为回归，不扩写旧版本迁移矩阵。 |
| 提示更新 | 能力开关四组合；删除旧“无固定格式/runtime改写”断言；确认新资源被 compose，并由现有 fresh Idle 路径接纳一次。无需新增跨版本 Prepared 场景。 |
| 真实调用 | 五份邮件对照继续留档；新增合成多 Note、混合 Mail/Note、反例和大字面量正文。每项报告循环次数、tokens、耗时、错误与人工边界审核，不要求整个随机 provider 实验没有任何 transport 失败。 |

测试入口：[TextExtractorTests](../../tests/Galatea.Server.Tests/TextExtractorTests.cs)、[Mail tests](../../tests/Galatea.Server.Tests/GalateaMailboxTests.cs)、[Note tests](../../tests/Galatea.Server.Tests/CharacterNoteExtractorTests.cs)、[Note live tests](../../tests/Galatea.Server.Tests/CharacterNoteTranscriptionLiveTests.cs)、[TrackedPromptTemplateTests](../../tests/Galatea.Server.Tests/GalateaTrackedPromptTemplateTests.cs)、[AdmissionRetryTests](../../tests/Galatea.Server.Tests/GalateaAdmissionRetryTests.cs)、[SessionProvisioningTests](../../tests/Galatea.Server.Tests/GalateaSessionProvisioningTests.cs)。

## term `Implementation-Handoff` 实施切片与压缩后接续

### spec [A-WORK-PACKAGES]

| 工作包 | 写入范围与完成条件 |
|:--|:--|
| P1 原文行映射 | 新 source lines / extraction input 类型，确定性 renderer/slicer 与容量测试；不依赖真实模型。 |
| P2 通用执行 | TextExtractor 的显式策略、admission 和有界 loop；四消费者策略声明；SingleCompletion 回归；完成异常/重复/预算测试。 |
| P3 Mail 与 Note | 新 range wire DTO、业务 mapper、合同版本与源顺序；保留各自 ledger API。可以在 P1/P2 的接口明确后分别施工，再集成。 |
| P4 GM prompt | 按能力门控更新三个资源，修正 Note 忠实代写文档的现行状态和 prompt tests；复用现有 Setup 更新入口。 |
| P5 集成、实验与单实例切换 | 跑相关持久化/回执回归；让隔离 replay 使用生产实现或同一共享引擎；补 Note/混合样本并审核。实现验收通过后，对唯一开发实例执行既有安全停止点确认、备份、重建启动和首次新处理检查。无独立迁移工具或 P6 升级工程。 |

施工先检查工作区和相关 AGENTS，按 P1/P2 → P3/P4 → P5 顺序。代码边界清楚后可并行 Mail、Note 与 prompt 包，最后串行 .NET 集成验证。将最终实现与本设计的任何实质差异写回本文，尤其是 loop 失败策略、范围语义与 capture 合同。

### derived [A-RESUME-CHECKLIST]

压缩后从本文继续，避免重新调查：

1. **产品代码已改为行号 + Tool-Loop。** 旧 ignored `replay/` 已冻结为历史对照，新 `production-replay/` 直接调用正式提取器；两者不能混为同一实现。
2. 已选方向：Mail/Note 行号范围 + Tool-Loop；GM 干净正文块；宿主原文切片；无全文 Contains；不做本文以外的 ConnectionState/DerivedInfo 协议迁移。
3. 重点审查 P2：仅完整批次返回；成功 ack 在物化/验证之后；重复 source occurrence 不二次捕获；首版不搬宽松 invalid-call 自动修正。
4. P3/P5 按单实例停止升级：旧持久 DTO 原样读，新提取器直接替换；复用既有 capture 幂等与 prompt Setup，不再展开旧 capture / Prepared 跨版本迁移。五份历史漏信不会自动补发。
5. 所有样本正文及细粒度差异留在 ignored 目录；tracked 文档和合成测试不得混入真实角色正文或凭据。
6. Note、多产物上限与本版失败原子性已有确定性测试；真实调用另有独立边界审核。后续扩大样本时继续用产品重放器，不改为旧实验循环，不重新展开历史兼容工程。

## term `Implementation-Evidence` 实施与验证记录

### derived [A-PRODUCT-IMPLEMENTATION]

P1–P4 已实现。P5 使用 `.atelia/exports/galatea-five-mail-actions-20260926/production-replay/` 直接调用产品 Mail / Note，而非复制循环。保留原 `replay/` 的旧文本提示词快照，只用于历史对照。

实施中收紧了三处边界：

- 默认 180 秒整批 deadline 仅作用于 `UntilNoToolCalls`；`SingleCompletion` 保留原有 client retry 生命周期，构造时仍可显式设置 deadline。
- 内部 deadline 到期是 typed `DeadlineExceeded`；调用方取消保留原 caller token，不误报为持久状态损坏。
- `UnrepresentableLayout` 在首次 post-completion 就报告维护错误。Mail / Note 的异常携带原 `ExtractionSource`，Release 也保留来源身份；不能等下次 admission 才暴露。明确请求重试仍不等于修复不可变正文。

新增 / 迁移的测试覆盖行映射、opaque reasoning 回灌、64 次原始调用与零调用收尾、16 条 Note、跨轮预算、幂等与冲突、并发隔离、拒绝旧全文字段、取消 / deadline、Note 收尾失败零保存且重试跳过已捕获 Mail、首次布局失败及来源身份。GM 四能力组合和既有 fresh Idle Setup 更新路径一并回归。

### derived [A-DEVELOPMENT-INSTANCE-BACKUP]

切换前已确认无实例打开句柄；现有 CLI 的只读 `validate` 证明两个 session 均为 Idle，且各自当前 head 已有 Mail 与 Note 的零产物 capture。无须补提取或调整 baseline。

备份目录（ignored）：`.atelia/backups/galatea-mail-note-ranges-20260925T211029Z/`。完整复制唯一实例，并逐一核对 203 个文件的 SHA-256；目录内保留 manifest 及两份只读 validation report。

### derived [A-PRODUCTION-REPLAY-VALIDATION]

2026-09-26，实际产品提取器，`gpt-6-luna` / High，14 个案例各一次、并发 3。结果目录：

```text
.atelia/exports/galatea-five-mail-actions-20260926/production-replay-runs/20260925T212150Z-98fee8da/
```

| 案例 | 结果 |
|:--|:--|
| 五份原始邮件 Action | 5/5 正常完成；每份两封，10 封正文与独立参考边界完全一致，收件人顺序正确。 |
| Tool-Loop 的实际作用 | 170155、053437 都在第二轮才取得第二封，第三轮零调用结束。 |
| 多 Note、混合 Mail/Note、大字面量 | 5/5 正常完成，7 条正文边界正确；保留 TAB、`>`、空白、引号、路径与校验字符串。 |
| 草稿 / 他人旧示例 / 无当前请求 | Mail 与 Note 各一个反例，均无工具调用、零产物。 |
| 正文与叙述混在同一行 | Mail 与 Note 各一个反例，均明确 `UnrepresentableLayout`；没有成功零 capture。 |

12 个可正常完成的案例全部完成，17 条正文经过独立原文范围核验；另外 2 个按预期明确拒绝。人工检查了历史邮件的实际寄出 evidence、主题、回复 ID，以及合成 Note 的授权与内容边界，未发现边界错误。此为单次 canary，不表示长期成功率 100%；约 25 KiB 的长正文也不替代确定性容量极限测试。

26 次 Completion 均报告了 usage：input 146,384 tokens、output 2,156 tokens。完整请求、响应、工具参数、来源、返回正文与逐轮诊断均已落盘（目录 0700、文件 0600）；范围合法性、参考正文边界、运行完成和预期拒绝分别计数。重放器采用有界在线统计，`--repeat 0` 可长期运行，不在内存中累计全部历史记录。

复跑入口（不写业务数据库，不投递邮件）：

```bash
dotnet run --project .atelia/exports/galatea-five-mail-actions-20260926/production-replay/ProductionReplay.csproj --no-build -- --repeat 20 --parallel 3
```

### derived [A-TEST-AND-CUTOVER-RESULT]

- Debug 全部 Galatea.Server.Tests：**1,387 passed、2 skipped、0 failed**（共 1,389）。首次全量发现的三处旧测试夹具 XML 比较问题已修复，最后全量重新通过。两项 skip 为 opt-in Codex delegation live 和只在 Release 执行的 capture 诊断测试。
- Release 单独运行 capture 无诊断与布局异常来源身份两项测试：**2 passed**；Release 构建无警告/错误。其他 opt-in live 测试未启用，本次真实模型覆盖来自上面的产品重放器。
- 新产品重放器与历史 replay 均无警告/错误构建；历史 replay 的离线自测通过，旧全文协议仅在 ignored 基线快照中保留。
- `git diff --check` 通过。文档检查仍报告 4 个改造前已有的失效链接，位于 `control-receipt-simplification-plan.md` 与 `recap-store-simplification-plan.md`；本次触及文档无新增诊断。

已在备份和验收之后启动唯一开发实例的新版 Release 服务，监听原配置 `http://127.0.0.1:3511`。登录、两个角色 recent-turns / agent / mailbox 读取及 `retry-admission` 检查均成功；两角色 `waiting`、code 为空。该检查不创建新轮次：cyber 的 Mail/Note capture 各 155，gpt 各 284，检查前后相同；所有现存 SessionJournal 文件哈希保持不变。未补发历史漏信、未修改配置、未重置 baseline。

GM 资源已经编入新版二进制，**真实实例的 Setup 文本将在下一次正常 fresh 轮次沿既有 reconciliation 接纳**；本次没有为验证而生成故事轮次。首次 fresh 接纳一次的流程已有隔离 host 回归覆盖，不能把它与“真实实例已生成新版 GM Action”混为一谈。

验证日志、启动 PID / 日志及 HTTP smoke 记录保留在 ignored 目录：

```text
.atelia/exports/galatea-five-mail-actions-20260926/production-validation/
```

源码、测试与文档改动保留于工作区，未提交或推送。
