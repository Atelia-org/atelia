# Galatea 主 system prompt source 导航

状态：**Current source ownership router**  
Current contract：[Galatea root config V5](../../SessionJournal/current/contracts/galatea-root-config-v5.md)

Galatea 主system prompt不是一份可由operator整体覆盖的文件。Current binary按固定顺序组合三份source：

1. [`trpg-protocol-prefix-zh-cn.md`](trpg-protocol-prefix-zh-cn.md)：Galatea.Server embedded、code-owned；
   定义TRPG GM、voice/output grammar与GM carrier来源边界。
2. [`character-context-standard-zh-cn.md`](character-context-standard-zh-cn.md)：bootstrap starter；复制到
   operator配置路径后由operator拥有，只保存世界观、人物设定与长期记忆。
3. [`trpg-mailbox-protocol-suffix-zh-cn.md`](trpg-mailbox-protocol-suffix-zh-cn.md)：Galatea.Server embedded、
   code-owned；定义界外邮箱叙事协议。

Exact composition为：

```text
prefix + "\n\n---\n\n" + operator context + "\n\n---\n\n" + suffix
```

拼接后只执行一次closed `${characterName}` / `${playerName}` renderer。这里没有第四份完整prompt副本、
include/module engine、基于Markdown heading的动态路由或operator protocol override。Operator prose不是安全边界；
runtime不会解析context中的H2来启用或禁用feature。

同目录RecapGrid maintainer prompts由各自asset/resource owner管理，不参与上述主system prompt composition。
