# Galatea 主 system prompt source 导航

状态：**Current source ownership router**  
Current contract：[Galatea root config V5](../../SessionJournal/current/contracts/galatea-root-config-v5.md)

Galatea 主system prompt不是一份可由operator整体替换的文件。Current ownership涉及四份tracked resource：

1. [`trpg-protocol-prefix-zh-cn.md`](trpg-protocol-prefix-zh-cn.md)：Galatea.Server embedded、code-owned；
   定义TRPG GM、voice/output grammar与GM carrier来源边界。
2. [`character-context-standard-zh-cn.md`](character-context-standard-zh-cn.md)：bootstrap starter；复制到
   operator配置路径后由operator拥有，保存世界观、人物设定与独立人工长期记录，并解释RecapGrid派生context
   与newer raw History的来源优先级。
3. [`trpg-mailbox-protocol-base-zh-cn.md`](trpg-mailbox-protocol-base-zh-cn.md)：Galatea.Server embedded、
   code-owned；始终定义接收界外来信的通用收件匣。
4. [`trpg-outbound-mail-protocol-appendix-zh-cn.md`](trpg-outbound-mail-protocol-appendix-zh-cn.md)：
   Galatea.Server embedded、code-owned；仅当validated `galatea.outbound-mail-extractor` binding非`null`时追加，
   定义Codex主动投递协议。

Exact composition为：

```text
prefix + "\n\n---\n\n" + operator context + "\n\n---\n\n" + mailbox base
[when outbound binding is non-null: "\n\n" + outbound appendix]
```

组合后只执行一次closed `${characterName}` / `${playerName}` renderer。这里没有完整prompt副本、include/module
engine、基于Markdown heading的动态路由或operator module field。Character-context fields不能移除、替换或重排
validated binding所选的code-owned bytes；但operator context与protocol位于同一trusted system message，prose
可以在语义上冲突，因此ownership不构成
prompt-level安全隔离。Runtime不会解析context中的H2来启用或禁用feature。

同目录RecapGrid maintainer prompts由各自asset/resource owner管理，不参与上述主system prompt composition。
