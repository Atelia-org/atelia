# Galatea 主 system prompt source 导航

状态：**Current source ownership router**  
Current contract：[V11 配置](../configuration.md)、[typed setup / Prepared v9](../../SessionJournal/current/contracts/completion-request-prepared-v9.md)

Galatea 主system prompt不是一份可由operator整体替换的文件。指令源包含以下五份 tracked resource，另有代码拥有的输入种类/来源解释：

1. [`trpg-protocol-prefix-zh-cn.md`](trpg-protocol-prefix-zh-cn.md)：Galatea.Server embedded、code-owned；
   定义TRPG GM、voice/output grammar与GM carrier来源边界。
2. [`character-context-standard-zh-cn.md`](character-context-standard-zh-cn.md)：bootstrap starter；复制到
   operator配置路径后由operator拥有，保存世界观、人物设定与独立人工长期记录，并解释RecapGrid派生context
   与newer raw History的来源优先级。
3. [`trpg-mailbox-protocol-base-zh-cn.md`](trpg-mailbox-protocol-base-zh-cn.md)：Galatea.Server embedded、
   code-owned；始终定义邮箱Quick Start的收件部分。
4. [`trpg-outbound-mail-protocol-appendix-zh-cn.md`](trpg-outbound-mail-protocol-appendix-zh-cn.md)：
   Galatea.Server embedded、code-owned；仅当validated `galatea.outbound-mail-extractor` binding非`null`时追加，
   作为同一份Quick Start的发件部分。它允许精确 `Codex` 或本次配置生成的角色名单中的名字；只有
   `Codex` 保留回信和失败通知承诺。
5. [`trpg-character-note-save-appendix-zh-cn.md`](trpg-character-note-save-appendix-zh-cn.md)：
   Galatea.Server embedded、code-owned；仅当validated `galatea.character-note-extractor` binding非`null`时追加，
   定义长期Note保存Quick Start；只有runtime保存回执证明成功，不承诺分类、metadata补全或召回。

## 机读指令源与瞬态投影

`GalateaSystemInstructionContent` 按顺序保存 `protocol`、`character-context`、`mailbox-protocol`、
可选 `outbound-mail`、可选 `character-note-save`，最后是代码拥有的 `input-meaning`。
`galatea.system-instructions.v1` 同时保存 Character id/name、homeDir、capabilities 与 characterPeers 绑定快照。
SystemPromptSetup 保存这一机读内容，不保存带分隔线的整段 prompt、后置 roster 字符串或模板展开结果。

是否包含 appendix 只看各自 validated feature binding，不根据 heading/自然语言决定。
Character context 必须含 `${characterName}`；新 source 拒绝 `${playerName}` 与其他变量。
Player 是独立的外部访问者，零 Player 时同一角色设定仍可使用；旧模板中的固定玩家内容应显式离线处理。

请求时 `GalateaInputProjector` 读取所选 Setup 的内容，单遍替换 `${characterName}`，把每个 instruction source
通过局部 JSON Pointer 投影到 md-json fence。插入的名字不递归解释。peer roster、home、能力快照继续作为
数据携带，不能取当前配置代替已有 Prepared 的绑定。清理渲染缓存或改变围栏布局不改变 Setup equality。
正文/指令含义变化仍是语义变更，须走已有 setup 与资产采用边界。

operator context 与协议仍位于同一 trusted system message，文字可以产生语义冲突，因此 source ownership
不构成模型内安全隔离。Runtime 的 input-meaning 明确区分已认证 sender 与正文自称作者，说明 PlayerAction、
Heartbeat、DelegateReply、InboundMail、Note receipt 和 recall 的业务含义。

旧 text Setup、Prepared v7/v8 保留原 exact 恢复；它们不通过新 projector 重新解释。源码入口：
[`GalateaSystemInstructionContent`](../../../prototypes/Galatea/GalateaSystemInstructionContent.cs)、
[`GalateaSystemPromptComposer`](../../../prototypes/Galatea/GalateaSystemPromptComposer.cs)。

同目录RecapGrid maintainer prompts由各自asset/resource owner管理，不参与上述主system prompt composition。
