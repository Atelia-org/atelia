# Character Note 原文范围提取

更新：2026-09-26。本文维护当前提取边界；完整合同见[Mail / Note 行号提取设计](mail-note-line-range-design.md)，保存与恢复合同见[Default MemoPod](character-note-default-memopod-v1.md)。服务须重新构建并重启才采用新实现，GM 提示词通过既有 Idle Setup reconciliation 在下一次正常处理前更新。代码改动和测试不表示真实实例已启动或产生新 capture。

## 决策与来源

角色通过自然叙事表达当前保存意图；GM 在同一 Action 中写好最终正文，TextExtractor 识别意图与角色归属并返回正文行范围，宿主按原文切片。模型不再复写正文，也不通过全文包含检测、模糊匹配或归一化证明准确性。

采用的最小流程是：

```text
本轮角色叙事
  -> 宿主添加虚拟行号
  -> 有界 Tool-Loop 识别每条 Note 的连续整行范围
  -> 检查范围、切片与容量；按来源位置去重和排序
  -> 零工具调用结束完整提取批次
  -> 首次 durable capture
  -> 原样保存到 MemoPod
  -> 已保存的 Note 内容回执
```

保存请求仍不需要专门的“提交动作完成”句式。GM 将每条 Note 的最终正文写在独占行的`[Note正文开始]`和`[Note正文结束]`之间，保存请求与外部叙述留在块外；不自行生成提取行号。标记只约束布局，不建立保存授权。当前Action须说明保存意图和完整内容；仅有未来计划、草稿、引用旧Note、他人行为或故事内普通书写不产生新保存请求。一次请求可以包含多条Note，提取以Note而非请求为单位。上述排除条件只判断当前请求是否存在，不排除被明确要求保存的计划、回忆、旧记录或保存经验。

Extractor 不去包装、重排段落、改写或拼接。未使用标记的 Action 仍可由同一个提取器识别，只要完整正文可以表示为连续整行范围。对于明确合法但正文无法如此表示的当前请求，模型报告`unrepresentable_layout`并使批次失败，不截短或伪装成零结果；对是否存在合法请求本身不确定时仍保守地不提取。XML实体、Markdown、空白和原有换行直接保留在切片中；输入展示的 JSON escaping 不改变原文。

## 实现边界

- wire 工具`emit_character_note_range`只有`textStartLine`与`textEndLine`，采用 1-based、包含首尾行的坐标。物化后的`CharacterNoteIntent`仍只有`Text`；不重新引入`EvidenceQuote`。切片包含范围内部的原始换行，排除所选末行之后的换行符。
- runtime保留非空、有效Unicode、每条64 KiB、每批16条及总正文256 KiB上限。provider或结构失败不能伪装为成功的空结果。
- 新 capture 的`Text`来自原始 Action 切片，在首次capture时成为持久层的`ExactText`，此后逐字恢复与保存。旧 capture 的正文保持原样，不追溯要求它来自原文切片。
- 只有完整 Tool-Loop 成功结束才返回批次；已取得部分 Note 后发生的结构、范围、业务、provider、取消或预算错误仍使整个本次提取失败。工具接收回执只确认候选，不声称已持久保存。
- 不改SQLite schema、MemoPod格式、capture身份、幂等恢复或回执outbox；无需数据库迁移。旧capture和冻结回执按原样恢复，不按新prompt重提取或重渲染。
- 新回执称“已保存的 Note 内容”，只报告真实保存结果；正常展示完整保存正文，超出展示预算时明确列出保存标识。

真实提取失败仍可能阻止新轮次：当前恢复只检查latest terminal Action。直接忽略失败并推进head会使旧请求失去重试入口，因此不能以解除阻塞为由跳过未完成提取。用户发起的独立重试复用既有reconciliation；接口与操作说明由[Server API](server-api.md)和[运行指南](../../prototypes/Galatea/README.md)维护。不可表示的正文布局属于非瞬态错误，重试同一不可变 Action 通常无法修复，须明确处理，不能自动跳过。

## 验证与能力限制

单元与运行时测试验证原始切片、非法范围失败、跨轮枚举及重复处理、批次完整性、保存恢复不重复以及回执诚实。固定 provider 结果不能证明真实模型会选对全部边界；模型仍可能漏识意图或选错一个合法范围。

真实模型入口为`CharacterNoteTranscriptionLiveTests`，默认跳过。它只使用合成叙事，分别检查两条独立Note、跨段且带数字/否定/中文路径/实体字面量的Note、仅引用旧Note。数量断言之外，输出正文写入本地报告供人工评读：

```bash
ATELIA_RUN_GALATEA_NOTE_LIVE=1 \
ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE=/root/.codex/auth.json \
ATELIA_GALATEA_NOTE_LIVE_REPORT=/tmp/galatea-note-live-20260926-unique.jsonl \
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj \
  --no-build --no-restore -m:1 -nr:false \
  --filter FullyQualifiedName~CharacterNoteTranscriptionLiveTests
```

执行前先构建；报告路径替换为本次独立文件。真实调用结果与人工评读应单独记录，有限样本不构成luna或后续模型能力保证。完整范围切片消除了逐字复写错误，不能替代对提取完整性与语义边界的审核。

## 历史：2026-09-13 忠实代写方案

此前方案让 TextExtractor 忠实整理正文，允许移除外围排版和归整段落，移除了正文及辅助证据的 substring 门禁。原因是 XML 实体或 Markdown 差异会使正确意图的整个批次失败并阻止自主活动。该方案已由行号引用取代；其意图资格、持久采纳正文、旧 capture 恢复和真实保存回执边界继续保留。以下是旧版验证证据，不代表本次行号版验收。

2026-09-13真实验证：首次样例运行漏提第二条，暴露了“每个request一个call”与一次请求包含多条Note的歧义，以及资格排除规则可能误套正文的问题。修正提示的输出单位和作用域后，原样三个样例的输出数量分别为2、1、0，测试通过。人工评读确认两条Note完整分立；跨段Note保留数字10、否定与条件、中文路径`/试验/记录.md`以及字面量`&gt;`；引用旧请求不产生新Note。最终报告为本机`/tmp/galatea-note-live-20260913-v2.jsonl`，只含合成样例结果。本次没有通过真实用户会话验证，也没有写入用户记忆库。

同次隔离验证还完成：Galatea.Server.Tests全套890通过、1项显式live gate跳过；前端三个测试入口共17项通过；构建零警告、零错误；文档检查30文件零诊断。新增运行时用例覆盖两条整理正文真实落库、冻结回执、冷重开换提取契约不重提，以及失败后独立重试、并发busy、取消/停止、保留reply与runtime恢复阻塞。恢复原本挂起的用户Note仍须在新服务启动后由正常处理路径完成，不能把合成测试通过等同于该用户数据已保存。
