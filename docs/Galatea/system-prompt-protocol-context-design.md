# Galatea code-owned protocol 与 operator character context 设计

状态：**Implemented；current root contract is V6**  
日期：2026-08-29  
实现基线：V5 ownership `b0760310`；Character Note save prompt hard cut `78385612`；durable save closure `82414433`  
Current contract：[Galatea root config V6](../SessionJournal/current/contracts/galatea-root-config-v6.md)

## 1. 结论

Galatea 主system prompt不再由operator整体拥有。Current binary固定组合：

```text
code-owned TRPG protocol prefix
        + "\n\n---\n\n"
operator-owned character context
        + "\n\n---\n\n"
universal code-owned mailbox base
        + when validated outbound binding is non-null:
          "\n\n" + code-owned Codex outbound appendix
        + when validated Character Note binding is non-null:
          "\n\n" + code-owned Character Note save appendix
        -> one closed name render
        -> finalized SystemPrompt
```

Root config因此hard-cut到V5，并用`characterContextTemplate` /
`characterContextTemplateFile`取代V4的`systemPromptTemplate*`。Runtime DTO与SessionJournal仍只接收finalized
`SystemPrompt`，不引入分段durable identity或schema。Mailbox base与两个capability-gated appendix物理上是独立
resource，启用对应binding时向模型连续呈现为简短Quick Start。

## 2. 为什么需要拆分ownership

旧完整prompt同时混合两种authority：

- runtime必须依赖的GM carrier、voice/output grammar、来源边界与邮箱协议；
- 每个user自行维护的世界观、人物设定、关系背景与长期记忆。

前者与parser、extractor、Recap evidence规则及generated Observation的真实runtime行为耦合。如果operator文件
遗漏、复制旧版或两个user各自修改，就会让模型可见协议与code-owned consumer分叉。后者是故事内容，必须允许
不同user独立编辑，不能随binary发布覆盖。

V5把这条边界直接写进field language：operator字段名只声称自己拥有character context，不再声称能够替换整个
system prompt。项目尚未发布，hard cut比保留双字段、默认或兼容reader更简单可靠。

## 3. Protocol/context resources的exact ownership

### 3.1 Protocol prefix

[`prompt/trpg-protocol-prefix-zh-cn.md`](prompt/trpg-protocol-prefix-zh-cn.md)由Galatea.Server作为embedded
resource拥有。它定义TRPG GM职责、两条叙事流、`[character]`/`[旁白]`/`[状态摘要]`输出grammar及GM carrier
来源边界。Operator不能通过`characterContextTemplate*`移除、替换或重排这段bytes。

### 3.2 Character context

Operator context保存世界观、人物设定、关系背景和可人工维护的长期记忆。Current bootstrap seed是
[`prompt/character-context-standard-zh-cn.md`](prompt/character-context-standard-zh-cn.md)；复制到配置路径后，
该文件就是operator authority，不再由binary更新。

Context必须nonblank且至少包含一次exact `${characterName}`；`${playerName}` optional。Inline保持exact；有效file
覆盖inline并在strict UTF-8 decode后`Trim()`。Standard context明确说明较早History由RecapGrid派生为带来源的
world-understanding与first-person-autobiography context、冲突时newer raw History优先；下方自主记忆则是独立
人工长期记录，未来由动态外部记忆机制接管。

Context与code-owned protocol最终处在同一trusted system message。Structural ownership保证character-context
fields不能删除或重排validated binding所选的code-owned bytes，但operator prose仍可能在语义上与协议冲突；
这里没有prompt-level安全隔离承诺。Runtime也不会
解析context H2来判定feature或权限。

### 3.3 Universal mailbox base

[`prompt/trpg-mailbox-protocol-base-zh-cn.md`](prompt/trpg-mailbox-protocol-base-zh-cn.md)由Galatea.Server
embedded resource拥有。它始终定义角色如何理解和接收界外来信，只承诺收件匣、阅读、忽略与保存；不承诺主动
发送、Codex route或未来回信。它以`## 界外邮箱`开始逻辑Quick Start。

### 3.4 Conditional Codex outbound appendix

[`prompt/trpg-outbound-mail-protocol-appendix-zh-cn.md`](prompt/trpg-outbound-mail-protocol-appendix-zh-cn.md)
也是Galatea.Server embedded、code-owned。仅当Completion-owned sibling config已经通过Galatea routing validation，
且exact `galatea.outbound-mail-extractor` binding非`null`时，composer才追加该段。它定义当前唯一可投递recipient
`Codex`、完成发送的叙事证据与后续回合的回信/送达失败语义。它以`### 发信给 Codex`继续同一份
逻辑Quick Start，不是第二份独立的模型使用协议。

这个布尔选择来自现有validated host binding，不是operator可排列的prompt module，也没有per-user开关。

### 3.5 Conditional Character Note save appendix

[`prompt/trpg-character-note-save-appendix-zh-cn.md`](prompt/trpg-character-note-save-appendix-zh-cn.md)
也是Galatea.Server embedded、code-owned。仅当exact `galatea.character-note-extractor` binding非`null`时，composer
才在outbound appendix之后追加该段。它教`${characterName}`如何明确提交长期Note完整原文，并说明只有后续runtime
发出的`Note 保存回执`才能证明保存成功；没有回执不能判断成功或失败。它不承诺分类、metadata补全或召回。

Note appendix presence与outbound独立，两个binding形成四种exact composition。是否追加同样只来自validated sibling
binding，不看operator prose或Markdown heading。

## 4. Fixed composition，而不是文档解析

`GalateaSystemPromptComposer`先固定组合
`prefix + "\n\n---\n\n" + context + "\n\n---\n\n" + mailboxBase`。Outbound binding非`null`
时先追加`"\n\n" + outboundAppendix`；Character Note binding非`null`时再追加
`"\n\n" + characterNoteSaveAppendix`；随后调用已有`${characterName}` / `${playerName}` renderer一次。
External context与每份embedded resource有1 MiB读取cap，composite source与final rendered prompt也分别受1 MiB
cap；embedded resources还要求BOM-less、LF-only、nonblank strict UTF-8。

Markdown heading只帮助LLM与人阅读，不是machine discriminator。Runtime不会扫描`## 世界观`、`## 自主记忆`、
`## 界外邮箱`、`### 发信给 Codex`或`### 保存长期 Note`来决定分段、权限、feature或安全性。
是否追加两个appendix分别只看对应validated sibling binding，不看operator prose。

## 5. 为什么不采用更通用的方案

- **不保留`systemPromptTemplate*`并悄悄改变语义。** 同一个V4字段此前表示完整prompt；复用会让旧文件被再次
  拼接协议，或迫使runtime猜测并剥离自然语言。
- **不拆成独立world/memory文件。** 这里指operator context内的world/persona与人工长期记录；两者仍是同一个operator、同一次load、同一final setup authority，
  没有独立writer、权限或提交生命周期。拆文件只会增加顺序、partial-missing、bootstrap和bounds矩阵。
- **不做module/include engine。** 当前只有固定base composition和两个existing-binding-controlled appendix，不需要
  module ID、priority、operator optional list、include cycle、per-module digest或动态registry。
- **不解析H2。** Heading可由operator重写且存在于自然语言中，不能成为安全或durable contract。
- **不追求旧prompt bytes永远不变。** Finalized text真正变化时，由既有desired-setup reconciliation追加一次setup
  即可；为了zero setup保留兼容分支得不偿失。

后续Character Memory V1已经按真实需求建立独立writer与事务，并由
[Default MemoPod V1](character-note-default-memopod-v1.md)独立定义；这没有把prompt composition扩张为module engine。
若未来出现多个可选protocol，再按届时的authority与lifecycle设计新版本；V5不预留半套module机制。

## 6. Durable 与其他子系统边界

- New session provisioning用finalized prompt创建现有raw三个setup events。
- Existing Idle session只在下一次fresh turn通过`ReconcileDesiredSetup` exact比较；变化时append
  `SystemPromptSetup`，不改写历史。
- 停服修改sibling config并重启后的validated outbound或Character Note binding切换会改变对应appendix presence，并自然走同一个
  next-fresh exact setup rotation；不增加prompt config字段或durable event kind。
- Prepared/Frozen recovery继续读取historical governing setup与frozen request，不用current composition resources重组。
- RecapGrid V6仍由character/player names决定Definition/BuildTarget；主prompt拆分不旋转asset。
- Outbound与Character Note extractor继续各自使用code-owned prompt和ContractId；主prompt appendix不进入extractor identity。
- Prompt ownership本身不拥有Mail/delegation SQLite、SessionJournal、Completion或HTTP/SSE schema；Character Note
  save-receipt grammar的后续hard cut由Default MemoPod V1/runtime合同独立拥有。

## 7. Machine-local migration语义

V4 ignored development instance必须停服、备份config与active full-prompt files，再把root改成V5 fields，并从
每个完整prompt中只提取world/persona/memory context。Runtime不会自动修改ignored文件。

Current ignored `connections.json`的outbound binding非`null`，因此迁移后`cyber`与`gpt`都会得到code-owned
Codex outbound appendix。旧`cyber.md`已有等价邮箱段，正确切分可保持这部分语义；旧`gpt.md`没有，新增appendix
是与实际enabled feature对齐的预期变化，不需要per-user module开关。若binding改为`null`，两者仍保留universal
inbox base，但prompt不再承诺主动发送或Codex投递。未被config引用的`cyber_template.md`不阻塞迁移。

Character Note key是同一strict `bindings` object的required sibling；旧ignored文件缺key时必须由operator停服、备份后
显式补成`null`或exact connection ID，runtime不提供missing-key兼容。选择non-`null`会追加Character Note保存
Quick Start；实际保存与回执仍由runtime durable apply边界负责。

迁移不provision/rebuild/promote RecapGrid，不运行outbound mail extraction，不改delegation store或raw SessionJournal。
Read-only canary应验证load、final composition与现有readiness；真正的setup rotation只发生在之后的普通fresh turn。

## 8. 验证矩阵

| 维度 | 必须证明 |
|:--|:--|
| Strict root | exact `v:6`；V1–V5、旧字段、unknown/wrong-case/duplicate/type mismatch拒绝；required `characterMemoryStateDir`建立path authority，实际open另受Note binding与writable-session gate控制 |
| Context precedence | valid file覆盖inline；inline exact；file decode后`Trim()`；missing/outside-root按contract失败 |
| Context grammar | nonblank、required character token、optional player token；unknown/malformed token拒绝 |
| Composition | exact prefix/separator/context/separator/mailbox base；两个non-null binding独立追加outbound/note appendix，both时顺序固定；只render一次 |
| Bounds/resources | external context、composite source与final output各1 MiB；embedded resource strict UTF-8、BOM-less、LF-only |
| Runtime DTO | 每个user只保留validated names与finalized `SystemPrompt`；appendix choice来自validated sibling binding |
| Bootstrap | missing in-root context create-only、shared path一次、existing no-overwrite、protocol不落operator目录；default outbound/note bindings为null |
| Durable setup | new repository保存final prompt；Idle及binding toggle只按exact变化rotate；Frozen使用historical setup |
| Cross-subsystem | Recap/mail/delegation/SessionJournal/Completion/HTTP durable identities与schema不变 |
| Governance | README、V6 contract、prompt router current；V5及更早合同保持准确历史 |
