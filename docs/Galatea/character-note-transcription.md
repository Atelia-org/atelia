# Character Note 忠实代写

日期：2026-09-13。本文维护当前提取边界；保存与恢复合同见[Default MemoPod](character-note-default-memopod-v1.md)。本次改动在独立worktree开发和验证，运行中的旧服务须在切换代码、重新构建并重启后才采用新行为。这里不表示本机服务已经部署。

## 决策与来源

用户的当前要求是：role-play角色通过自然叙事表达保存意图，TextExtractor承担代写工作，不以逐字符复制能力决定能否保存。调查发现，Note正文提取正确时，辅助证据的XML实体或Markdown差异也会使整批提取失败，随后阻止自主活动。

采用的最小流程是：

```text
本轮角色叙事
  -> 判断当前保存意图，忠实整理为 ordered text[]
  -> 检查结构和容量
  -> 首次 durable capture
  -> 原样保存到 MemoPod
  -> 已保存的 Note 内容回执
```

角色不需要固定格式或专门的“提交动作完成”句式。当前Action须说明保存意图和完整内容；仅有未来计划、草稿、引用旧Note、他人行为或故事内普通书写不产生新保存请求。一次请求可以包含多条Note，提取以Note而非请求为单位。上述排除条件只判断当前请求是否存在，不排除被明确要求保存的计划、回忆、旧记录或保存经验。

Extractor可以移除外围排版、归整多段文字，但必须保留事实、数字、人名、路径、否定、条件和不确定性，不增删事实、不摘要、不补全缺失信息。XML实体表示和正文中的字面量不同，不能统一做HTML解码来“修复”输出。

## 实现边界

- `CharacterNoteIntent`只有`Text`（JSON `text`）；删除`EvidenceQuote`及正文/证据的ordinal substring校验。语义忠实交给extractor，不增加模糊匹配器或第二个校验模型。
- runtime保留非空、有效Unicode、每条64 KiB、每批16条及总正文256 KiB上限。provider或结构失败不能伪装为成功的空结果。
- `Text`在首次capture时成为持久层的`ExactText`，此后逐字恢复与保存。`ExactText`的“精确”描述已采纳正文，不表示它一定是叙事Action的连续子串。
- 不改SQLite schema、MemoPod格式、capture身份、幂等恢复或回执outbox；无需数据库迁移。旧capture和冻结回执按原样恢复，不按新prompt重提取或重渲染。
- 新回执称“已保存的 Note 内容”，只报告真实保存结果；正常展示完整保存正文，超出展示预算时明确列出保存标识。

真实提取失败仍可能阻止新轮次：当前恢复只检查latest terminal Action。直接忽略失败并推进head会使旧请求失去重试入口，因此不能以解除阻塞为由跳过未完成提取。用户发起的独立重试复用既有reconciliation；接口与操作说明由[Server API](server-api.md)和[运行指南](../../prototypes/Galatea/README.md)维护。

## 验证与能力限制

单元与运行时测试验证格式归整可接受、非法结构仍失败、采纳正文与顺序精确保留、保存恢复不重复以及回执诚实。这样的测试使用固定provider结果，不能证明某个模型一定忠实或提取完整。

真实模型入口为`CharacterNoteTranscriptionLiveTests`，默认跳过。它只使用合成叙事，分别检查两条独立Note、跨段且带数字/否定/中文路径/实体字面量的Note、仅引用旧Note。数量断言之外，输出正文写入本地报告供人工评读：

```bash
ATELIA_RUN_GALATEA_NOTE_LIVE=1 \
ATELIA_CODEX_SUBSCRIPTION_LIVE_AUTH_FILE=/root/.codex/auth.json \
ATELIA_GALATEA_NOTE_LIVE_REPORT=/tmp/galatea-note-live-20260913-unique.jsonl \
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj \
  --no-build --no-restore -m:1 -nr:false \
  --filter FullyQualifiedName~CharacterNoteTranscriptionLiveTests
```

先在隔离worktree构建，再执行此命令；报告路径替换为本次独立文件。真实调用结果与人工评读应单独记录，有限样本不构成luna或后续模型能力保证。删除子串门禁也失去了一部分对数字、否定词误改的机械拦截，这是采用LLM忠实代写的明确取舍。

2026-09-13真实验证：首次样例运行漏提第二条，暴露了“每个request一个call”与一次请求包含多条Note的歧义，以及资格排除规则可能误套正文的问题。修正提示的输出单位和作用域后，原样三个样例的输出数量分别为2、1、0，测试通过。人工评读确认两条Note完整分立；跨段Note保留数字10、否定与条件、中文路径`/试验/记录.md`以及字面量`&gt;`；引用旧请求不产生新Note。最终报告为本机`/tmp/galatea-note-live-20260913-v2.jsonl`，只含合成样例结果。本次没有通过真实用户会话验证，也没有写入用户记忆库。

同次隔离验证还完成：Galatea.Server.Tests全套890通过、1项显式live gate跳过；前端三个测试入口共17项通过；构建零警告、零错误；文档检查30文件零诊断。新增运行时用例覆盖两条整理正文真实落库、冻结回执、冷重开换提取契约不重提，以及失败后独立重试、并发busy、取消/停止、保留reply与runtime恢复阻塞。恢复原本挂起的用户Note仍须在新服务启动后由正常处理路径完成，不能把合成测试通过等同于该用户数据已保存。
