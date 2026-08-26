# Galatea Codex 代行闭环重构状态

> 状态：Active
>
> 建立日期：2026-08-27
>
> 用途：记录 Galatea 通过叙事邮件向固定 Codex 代行者委派工作、并在后续玩家回合收到回信这一垂直闭环的已决策边界与未完成工作。

本文是会逐步收缩的施工状态页，不是长期设计规范或实施日志。每完成一个工作项，就应把已经稳定的契约迁入代码、测试或 `prototypes/Galatea/README.md`，再从“未完成工作”中删除对应条目。所有条目清空并完成端到端验收后，本文只保留完成声明和必要的权威链接，本阶段重构即告完成。

## 目标闭环

```text
前端触发 Galatea Turn N
  -> 主线 LLM 自由产生叙事 Action
  -> OutboundMailExtractor
  -> 0..N SendMailIntent
  -> exact recipient route
  -> 固定 Codex thread 的内存 FIFO
  -> codex app-server turn（后台运行，不阻塞 Galatea 等待 final）
  -> final response / delivery failure 写入内存 ReplyInbox

前端触发 Galatea Turn N+1 或更晚
  -> runtime 截取本轮开始时已经 ready 的回信
  -> 玩家 Action 与 0..N 代行者回信组成同一 Observation 的兄弟信息块
  -> 主线 LLM 继续产生叙事 Action
```

这个闭环让 Galatea 保持叙事角色与意图来源，Extractor 只翻译她已经表达并完成的发信行为，runtime 掌握能力授权，Codex 则作为外层真实世界的固定代行者。前端用户暂时负责触发下一次 Galatea 回合，不建立自动唤醒、定时轮询或 durable scheduler。

## 已决策边界

### 角色与授权

- 主线 Action/Assistant 仍是 TRPG GM composite carrier，不自动等同于 Galatea 本人的话语或行为。是否由 Galatea 实际完成发送，继续由 `OutboundMailExtractor` 按 `[Galatea]`、`[旁白]` 和 actual-send 语义保守判断。
- `SendMailIntent` 中真正交给路由器的业务内容是 `Recipient` 与完整 `Body`。现有 `Subject`、`InReplyToMessageId`、`EvidenceQuote` 可继续承担故事表达、关联和提取 provenance，但不得变成扩大执行权限的参数。
- 第一条可执行 route 使用 exact、case-sensitive canonical recipient `Codex`。未命中 allowlist 的收件人只保留为尚未投递的故事内候选，不触发外部副作用。
- 邮件正文只能成为 Codex turn 的 task input。`cwd`、sandbox、network、approval policy、Codex executable 和其他能力边界全部来自 code-owned route 配置，正文无权覆盖。

### 单一长期 Codex thread

- 初始阶段每个 `(Galatea user/session, Codex route)` 在当前 Galatea 进程生命周期内绑定一个 Codex thread。第一封命中邮件创建 thread，后续邮件始终 `continue` 同一 thread，使代行者自然保留协作上下文。
- 暂不实现 multi-thread、thread rollover、thread selection、conversation id 或按任务分叉。
- 暂不持久化 Galatea 侧的 `threadId` binding。Galatea 进程重启后允许创建新的 Codex thread；app-server 可能仍保留旧 thread，但本阶段不自动发现或认领它。
- 暂不实现主动长度管理。阶段性地依赖 Codex 自身当前的上下文管理行为，并接受长 thread 的潜在性能成本。官方 app-server 协议另提供 [`thread/compact/start`](https://developers.openai.com/codex/app-server/#trigger-thread-compaction) 手动压缩入口，但它不是本阶段的 Galatea 契约或未完成项。
- 同一 Codex thread 同时只启动一个 active turn。若同一 Galatea Action 或相邻 Action 产生多封命中邮件，runtime 按 `SourceActionHead` 与 `ArtifactOrdinal` 建立 per-thread 内存 FIFO，逐封执行；不得把多封邮件静默合并成一个 Codex turn。

### 异步执行与回信

- Galatea 主回合不等待 Codex final response。邮件被可靠接收入内存 coordinator 后即可继续完成 recent refresh、SSE `done` 与 `TurnLock` 释放。
- `dispatchId` 由稳定来源身份构成，至少包含 Galatea user/session、terminal Action head 与 artifact ordinal。重复观察同一 Action 不得启动第二次 Codex turn。
- 每次 Codex turn 的 terminal `agentMessage` final response 原样、受界限地保存在 Galatea 内存中，并保留对应 Codex `threadId`、`turnId` 与 `dispatchId` 供诊断和关联。MCP bridge 当前使用的 `AgentReport` output schema 不直接成为 Galatea 回信契约。
- Codex turn 的失败、被中断或无法产生合法 final response，应形成有界的 delivery-failure/退信结果；不能让 Galatea 永久误以为代行者仍在处理。
- 每次普通玩家回合开始时形成 ready-reply cutoff：cutoff 前已经 terminal 的全部回信进入本轮 Observation；之后完成的回信留到再下一轮。每封回信最多注入一次。
- 现有 authenticated `/api/v1/mailbox/inbound` 保留，但不参与 Codex 回执路径。它仍代表“外界主动来信并立即创建一个 Galatea turn”；Codex 回信只进入 ReplyInbox，绝不自动开启主线回合。

### Observation 表达

- 玩家 Action 与每封代行者回信必须是同一 runtime-owned Observation 中的兄弟信息块。回信不得拼入玩家原文，也不得伪装成 system/developer instruction。
- 内容面向 LLM 阅读而非程序回读，不采用 XML/JSON escaping。回信正文使用 `SessionContextContributionRenderer.RenderRecapBlock` 已验证过的 adaptive tilde-fence 算法：外层 fence 至少 4 个 `~`，且比正文中最长连续 `~` 多 1，从而允许正文中的 Markdown backtick fence、HTML/XML 字符和普通 Markdown 原样保留。
- 实现时应提取或暴露一个语义中立的 adaptive Markdown fence renderer，并证明现有 recap block 输出逐字不变。delegate reply 使用自己的 heading/info string，不冒充 `recap-block`。
- recent view、SSE `done.recent` 与 Undo 必须理解新的 composite Observation。玩家文本仍按普通玩家 turn 展示并保持 rewind eligibility；代行者回信以独立可读块展示。

### 配置与进程边界

- Extractor 使用的 LLM API connection 继续由 `.atelia/galatea/connections.json` 中的 exact binding `galatea.outbound-mail-extractor` 管理。
- Codex app-server 是 agent runtime/process integration，不是 `ICompletionClient` endpoint。其 recipient route、cwd 与 capability policy 放入单独的 strict、ignored machine-local 配置；当前建议文件名为 `.atelia/galatea/delegates.json`。
- 优先复用 `local-codex-mcp` 已有的 `CodexAppServerClient`、authentication、path policy、sandbox、server-request fail-closed、TaskStore 与 child lifecycle，而不是在 Galatea C# host 中复制一套 app-server JSON-RPC client。
- 建议给 `local-codex-mcp` 增加极薄的 Galatea sidecar entry：Galatea 拥有其进程生命周期，sidecar 直接调用底层 `CodexBackend`，并只跨进程传递 bounded dispatch/completion events；不让 Galatea 变成 MCP client，也不经过 Secure MCP Tunnel。

## 已有基础

- `TextExtractor`、typed artifact tool 与 `OutboundMailExtractor` 已实现。
- terminal Action durable 后已执行 bounded outbound extraction。
- `InMemorySendMailIntentBuffer` 已提供 Action-head batch dedupe 与容量边界，但仍是被动候选集合，没有 per-candidate dispatch lifecycle。
- `cyber.md` 已定义 Galatea 可自然召唤的界外邮箱以及“收件人、完整正文、完成发送”的叙事契约。
- inbound `MailboxMessage`、HTTP endpoint 与独立 Observation envelope 已实现并保留，但当前不接入代行者回执。
- `local-codex-mcp` 已实现并测试 app-server initialize、thread start/resume、turn start、notification correlation、final capture、sandbox/path policy、fail-closed approval 与 child-process lifecycle。

## 未完成工作

完成一项后，将其稳定语义移入相应 README/contract/test，并从本节删除，不在这里积累完成日志。

### In-memory dispatch and reply state

- 将被动 `InMemorySendMailIntentBuffer` 演进为 per-candidate lifecycle，或增加独立 coordinator，同时保留 Action-head + ordinal 去重。
- 实现每个 `(user/session, route)` 的单 thread binding、FIFO、单 active turn、容量边界、shutdown cancellation 与 bounded logging。
- 实现 per-user `ReplyInbox`、terminal completion sequence、ready cutoff、one-shot consume 与 delivery-failure。
- 明确普通 player Undo 对尚未 dispatch、正在运行、已完成未注入和已注入 exchange 的阶段性内存语义；首阶段不承诺跨进程恢复或 exactly-once。

### Galatea runtime integration

- 在成功捕获候选后 exact route 并非阻塞地交给 coordinator；未命中收件人不得产生 Codex side effect。
- 更新 `cyber.md`，告诉 Galatea canonical recipient `Codex` 是固定外界代行者、回信可能在以后某个玩家回合到达，并保持自然叙事而非输出协议文本。

### Verification

- fake sidecar/backend tests：首次创建、同 thread continue、FIFO、多封邮件、duplicate Action、unmatched recipient、failure/exit、late completion、one-shot reply injection 与 bounds。
- Galatea vertical tests：Action -> extractor -> Codex dispatch accepted -> SSE done 不等待 final -> final 入内存 -> 下一次玩家 Observation 含兄弟 reply block。
- gated live app-server canary：连续发送两封邮件，证明第二封复用第一封的 thread/context，并证明两次 final 分别只注入一次。

## 阶段完成条件

以下条件同时满足后，本节和“未完成工作”应收缩为完成声明：

1. Galatea 能在自然叙事中向 exact recipient `Codex` 发信。
2. 两封先后邮件在同一个 Codex thread 中执行，且无需重复交代已经存在于 thread context 的前因后果。
3. Galatea 主回合不等待 Codex final；前端下一次触发回合时，只消费 cutoff 前已经完成的回信。
4. Codex final 以未转义、可合法嵌套 Markdown fence 的兄弟信息块进入 Observation。
5. 未命中 route、重复 extraction、sidecar failure 和普通 Undo 不产生未经定义的重复执行。
6. focused automated tests 与一次 gated real app-server round trip 通过，限制与未承诺能力写入 Galatea README。
