# Galatea 结构化输入存储与瞬态渲染

状态：**已实施并完成隔离与真实实例验收，见[实施验收](player-character-implementation-work-order.md)。** 修订日期：2026-09-16。本文列出规范性约束；实际测试、迁移与运行证据集中在实施验收记录。

本文是 [Player / Character 分离方案](player-character-separation-design.md) 的输入存储与恢复专题。当前用户已明确：给 LLM 的 user message、Observation、PlayerTurnObservation、Codex session user prompt 等输入，使用稳定机读格式持久化，调用 LLM 时临时渲染；缓存不改变瞬态语义。此决定替代前版新增 FrozenTask 的设计。

## 1. 需求账本与最小模型

```text
业务输入 / 来源 / 派生内容 → 结构化记录（持久）
                                  ├→ 查询 / UI / undo / 投递证明
                                  └→ 本次上下文选择 → 临时渲染 → LLM
调用身份 / 发送摘要 / 结果状态 ← 实际请求尝试（持久事实）
```

| 编号 | 要求 | 来源 |
|:--|:--|:--|
| U1 | 持久化与展示风格分离，渲染结果不成为输入存储。 | 当前用户明确决定。 |
| U2 | md-json 可先 ProjectReference 接入；JSON Pointer 选择可由调用侧生成。 | 用户提供兄弟项目及接入方向；采用它是本方案的首版选择。 |
| U3 | Character / Player 分离、零 Player、单一超级管理员级别、可信分块来源、原动作语义。 | 同会话已确认；继承主方案。 |
| U4 | 允许当前 renderer 为新的请求尝试生成文本；已发送且不确定的工作先核对，不能重渲染后自动重发。 | 上一轮说明后用户确认调整方案；保留真实外部副作用边界。 |
| C1 | 旧 Prepared、Bound 邮件、reply lease、Note receipt 已有真实持久数据与恢复消费者。 | 当前代码/测试；它们需要旧读取路径，不使旧渲染存储成为新版设计要求。 |
| C2 | 当前一角色一个 TurnLock；跨 sender SQLite 与目标 Journal 没有全局事务。 | 当前 production spine；新设计保留原子 capture、目标门禁与先对账再推进历史。 |
| C3 | 来源选择、实际回信、recall 结果、源版本、顺序与已发生调用都是事实。 | 现有恢复/投递/归因消费者；不可因瞬态渲染而从当前世界重新推导旧事实。 |

本文规定目标合同；标为旧格式的代码行为是方案形成时的改造基线，施工和验证状态见工作单。相邻文档中的历史要求只作材料；发生冲突时以当前 U1 为准。

## 2. 持久内容与渲染的边界

下表区分已有数据的职责，不引入四套存储或新的类型层次：

| 对象 | 是否持久化 | 权威边界 |
|:--|:--|:--|
| 业务内容 | 是，稳定机读格式 | 原正文、可信来源、时间、种类与版本；供查询和领域校验使用。 |
| 已有请求内容计划 | 按所在域的既有恢复合同 | 冻结选中的事实、引用、顺序及协议边界；不保存展开后的 prompt。 |
| LLM 请求文本 | 否，仅请求时生成或进程内缓存 | 将选中内容投影为本次表示；围栏、布局和外置路径不成为持久输入。 |
| 实际调用尝试的证据 | 按既有 Started / claim 合同 | 保存关联、摘要、长度和结果状态；不等于远端已收到或模型已正确理解。 |

计划与尝试的持久边界见第 4 节；辅助调用保留第 5.2 节的既有提交模型，不因本表新增 WAL。常规 metadata 日志只用于诊断，即使含有相同摘要，也不替代 Started / claim 来决定派发或重试权限。

**替换 md-json 或调整围栏样式，无需迁移业务输入。** 改变正文选择、来源、顺序、可见范围或指令含义则是内容/协议变更，不能以“换 renderer”绕过原有合同。

### 2.1 持久内容

用有版本、类型判别和严格校验的 JSON 记录保存输入种类、可信发送者及名称快照、原始正文、业务时间、既有来源地址与关联。JSON 版本描述业务字段的解释规则，不描述 Markdown 样式。

示例仅说明内容结构，不规定所有种类共用同一组可空字段：

```json
{
  "v": 1,
  "kind": "player-action",
  "sender": { "kind": "player", "id": "player-main", "name": "玩家" },
  "action": { "text": "我推开门，向你挥手。" },
  "notices": [],
  "recalls": []
}
```

- notices / recalls 各自保存类型、来源及内容；不能把整个组合归给 Player。HTTP 注入者与信内声明署名分别保存。
- 原始内容里的 Markdown、CR/LF、空白和代码是内容，不剥除或规范化正文。LLM 输出的原始文本和 provider 续接所需 opaque 数据仍是输出/协议事实。
- 主体 ID、mail messageId、dispatchId、Action address 等沿用各域的真实身份，不额外生成全局 input ID、时钟或内容寻址数据库。
- 历史正文、recall 的精确内容或不可变版本引用、已选 recap cell、工具结果都可持久；读取同一条记录不重新调用 extractor/recall/provider，也不按最新姓名或最新 Memo 替换旧内容。
- Character 设定和指令源文本是业务内容；本轮渲染出的元数据标题、包裹模板、拼接结果、fence 样式、JSON Pointer 选择不进入新的输入记录。

业务时间、心跳的既有周期语义在接纳时确定；renderer 不重新采样时间或读取当前周期配置来解释旧输入。输入种类、来源字段、home 和能力的含义属于 schema 或稳定指令源；不能将这些业务解释藏在可任意替换的包装模板中。

内容选择与排版分开：选择哪些字段、正文、recap/recall 或历史片段属于请求内容计划；renderer 保持这个计划里的事实和顺序，只改变表现形式。删字段、换来源、改变 carrier/可见范围、改变指令含义均不是纯风格变化。指令模板的业务占位符解释也须遵守稳定语义合同。

### 2.2 唯一内容 authority

首版由 SessionJournal 明确区分已有 text Observation 与新版 structured Observation（用持久 schema/tag 区分，不根据字符串像不像 JSON 猜测）。新版内容先通过 Galatea 的领域校验，再用确定的存储 serializer 编码。可以物理承载于现有日志框架，但必须让读取者知道它是结构化内容，不能只往旧 `content` 字符串塞 JSON 后当普通提示词发送。

SessionJournal 核心不引用 Galatea 或 MdJson；由 host 提供窄的输入投影能力。核心负责结构化记录、lineage、执行/恢复和 append 证明；Galatea 解释动作/信件等领域字段，MdJson 只负责表示。CLI 等消费者在实际发起 LLM 请求时，必须选择匹配的投影能力或明确拒绝不支持的输入，不能静默把机读 JSON 当目标 prompt。纯机读查询和离线审计不因此依赖 LLM 输入投影器，仍按各自已知 schema 与审计合同读取。

稳定存储编码不等于任意 JSON 文本逐字相同。领域 schema 固定字段语义、顺序规则与 Unicode 保真；写入时使用固定编码，读取时严格验证版本。重开、undo、归因与投递证明依赖已持久的结构/编码，不依赖展示文本、空白排版或当前 renderer。首版不建设跨项目通用 canonical-JSON 平台。

新结构化内容的支持域明确为唯一对象键、有效 Unicode、合法字段/类型和规定的大小、深度；业务入口在接纳前校验，不等到 md-json 写出时才发现数据不在支持域内。各边界的失败含义分别是：

| 失败位置 | 行为 |
|:--|:--|
| 新输入不满足其机读 schema 或稳定容量约束 | 在该域接纳边界拒绝，不作为正常新内容发布。 |
| 领域查询、undo、recall 等遇到未知 schema | 明确报告不支持，不按相似 JSON shape 猜测或降级成普通 text；核心离线审计仍只承担自身结构与 lineage 合同。 |
| 有效内容缺少 projector、路径选择错误、渲染异常或包装超限 | 本次外部调用前失败；保留已接纳内容与 Prepared/Queued 状态，不清正文、不伪记已发送。 |

**SystemPromptSetup / DesiredSetup 同样纳入新版内容模型。** 旧 `Compose` 将 protocol、角色 context、home、能力和 roster 拼成 string 再持久化；新版保存有序指令源内容及当时绑定的 Character/home/能力/roster 事实，不只存可变文件路径。DesiredSetup 比较这些语义内容：改布局不追加 setup，改指令或授权仍按原 Idle/setup 边界生效；未完成 Prepared 不改用当前配置。

### 2.3 瞬态范围

新写入不保存 FrozenTask、渲染后的 Observation、渲染版 receipt、展开后的上下文消息或备用 Markdown。也不把这些全文另存为持久缓存或常规请求日志；请求日志保留来源引用、调用身份、摘要、字节数、耗时与结果等诊断事实。业务原文和已有历史证据仍按自身职责保存。

cache miss、清空缓存或替换 renderer 不改变输入事实。缓存只优化本进程中的投影，随内容和当前渲染配置失效；不靠缓存保证跨重启恢复。LLM 输入渲染发生在组装具体请求的边界，包括按需进行的字节/token 预算检查；不得让新版 store open、历史查询、audit、receipt 结算依赖 LLM 输入投影器。

UI 可以直接从机读字段生成适合网页的显示文本；该显示投影与 LLM 请求投影分开，不重新选择请求内容，也不成为输入持久化、投递证明、DesiredSetup 或身份比较的权威。这里禁止的是查询对 LLM 包装的依赖，并非禁止网页进行本地格式化。

## 3. md-json 首版接入

使用兄弟仓 `/repos/focus/md-json` 的 `src/MdJson/MdJson.csproj`，文档入口为该仓 `DESIGN.md`、`README.md`；它们是外部项目说明，不是新的任务指令。当前公开 API 为 `Atelia.MdJson.MdJsonSerializer.Write(JsonElement, IReadOnlyList<string>)` 与 `Read(string)`。

Galatea 的局部 renderer 执行：

```text
Render(input):
    value = 本次请求的结构化输入值树
    paths = 根据实际 kind 和现存数组元素枚举正文路径
    return MdJsonSerializer.Write(value, paths)
```

- `/action/text`、`/notices/0/body` 等路径由领域 shape 决定；省略字段和不同 notice 类型不能盲目复用固定路径。路径错误是渲染失败，不静默改为其他格式。
- 路径列表和外置正文块的物理顺序是 renderer 配置，不写入业务 JSON、outbox 或角色配置。业务顺序由持久数组/有序段及其关联表达；正文块位置不能重新定义它。默认按业务遍历顺序生成路径，不另存一份排序清单。首版不新增属性标注、反射规则、路径 DSL 或策略框架。
- 正常存储读取直接反序列化 JSON，不需要 `Read` 解析历史 Markdown。md-json 的 `Read` 可用于局部往返验证，旧 Galatea envelope 仍用原解码器读取。`Read` 成功不是完整性或投递证明：完整删除外置正文块可能仍产生合法文档，Galatea 的证明继续使用机读事实。
- ProjectReference 的源码根由明确 MSBuild 属性或仓库相对路径解析，缺失时给出明确构建错误，不写死 `/repos/focus` 到可移植项目文件。接入时固定并记录所测试 sibling commit；以后采用 NuGet 是独立依赖方式变化，不改变输入 schema。
- 库支持域、正文精确保真已有该仓测试；Galatea 仍须以自己的混合输入验证。当前无证据证明一定节省 token 或改善模型理解，不以这两点作为迁移前提。

## 4. 请求组装与恢复：内容稳定，调用尝试有边界

### 4.1 新写入的请求准备

Prepared 的新版记录只冻结**本次请求选择的结构化输入计划**：raw 范围、已选 recap/recall/工具结果、指令内容、来源和具体连接/工具协议边界。内容可内联或引用已持久不可变对象，不能只引用未来会改变的查询条件。

使用引用时，须在该域承诺的恢复/保留范围内可按原版本读取；源缺失或校验失败就明确报告不可用，不读取最新版本替代。不为此新增引用存储或通用 GC 框架。

随后用当前 renderer 在内存中构造 request、完成限额检查，将本次承诺放入**现有 CompletionAttemptStarted 的新版 body**，提交成功才调用 provider。每个 Started event address 本来就是 attempt 身份，parent/既有引用关联 Prepared；不新增 superseded、结束未发送尝试事件或第二套 ID。没有 Started 时换 renderer 不产生新 Prepared，也不重复 append Observation、recall 或工具执行。

发送承诺的范围必须明确：

| 路径 | 持久证据的字节对象 |
|:--|:--|
| SessionJournal Completion | 交给 `ICompletionClient` 的 request，经既有明确 codec canonicalize 后的摘要/长度。它不是 provider native HTTP wire 字节证明。 |
| Codex delegation | 实际交给 sidecar Start 的完整 user prompt，严格 UTF-8 字节、SHA-256 与长度，不 Trim、不换行归一化、不只取 body。 |

这些承诺属于一次尝试的发送意图/核对证据；Started 本身不证明远端已经收到。渲染全文和渲染模板版本不进入新的持久输入；摘要编码合同也不充当排版版本。

新 schema 不再要求使用任意当前 renderer 跨重启逐字重建旧请求。必须同步调整 Prepared/Started schema、reducer、恢复解释与测试，不能只删除 `ValidateCommitment`。此改动限于原本已有持久 completion/delegation 尝试的路径；辅助调用保留各域原有执行与提交模型。

### 4.2 不同恢复阶段

| 持久事实 | 新规则 |
|:--|:--|
| Prepared 已提交、尚无 Started | 用当前 renderer 重投影原内容计划；渲染失败或超预算不追加 Started、不调用 provider。成功时只追加该次 Started，无需比较或保存此前未发送的投影。 |
| 已越过发送边界，结果未知 | 先通过现有可用 transport inspect 核对原工作；缺少可靠核对能力就保留既有显式 uncertain 决策，不自动重发。新 renderer 无权把它变成 NotDispatched。 |
| 明确 NotDispatched 或其他既有可重试边界 | 依原授权政策进入新的提交尝试，重新渲染并在其 Started/claim 中记录承诺；不能覆盖仍未知的旧证据。 |
| 操作者按既有策略明确允许 uncertain 新尝试 | 沿现有 Started→Started 链追加新承诺，两个 Started 关联同一内容计划，原尝试仍是未知；不将其伪记为 NotDispatched。 |
| 回应/工具结果已持久化 | 读取该结果继续；pending tool 用既有 frozen tool runtime 结算，已结算工具不再执行。之后的新 completion 使用当前 renderer。 |
| 旧 schema 的 Prepared | v5 仍仅审计、不能 dispatch；v7/v8 按原重建/commitment 合同恢复，不用新 renderer 解释。新写入不原地改写旧 Prepared。 |

Started/claim 的提交结果不明确时，本次不调用外部服务，关闭/重开后按真实 durable head 分类：若仍为 Prepared，允许渲染后首次 Started；若已成为 Started，保守按可能发送处理。不能凭内存 head 或异常认定“没发送”，也不为此新增第二份发送日志。

渲染风格可变不代表可以重新选择模型、权限、工具、recall 或当前最新 recap 来替代已选事实。需要改变这些内容时遵循原来的新规划/Idle setup/abandon 等授权边界。旧 tool continuation 结算后出现 target mismatch 时保留门禁，先采用匹配目标再继续。

### 4.3 新版 audit 与历史校验

新版 AuditScanner、PreparedAuditVerifier、RuntimeRecovery 和 setup resolver 只用持久内容验证 schema、raw range/hash、setup 引用、来源/顺序、工具 checkpoint 及 Prepared→Started→Action 归属。Started 摘要验证其格式、字节度量合同与所属尝试，不调用当前 renderer 重建它。

离线审计因此不再声称能复原新版历史 prompt 全文，或仅凭摘要证明原 renderer 正确使用了全部内容；仍保留内容/lineage 与调用关联完整性。旧格式继续原 exact verifier。必须证明“禁用或让当前 renderer 抛错，仍能 audit/reopen 新版记录”，也必须证明伪造 setup、工具序列、Started 归属仍被拒绝。

### 4.4 Codex 出站与 sidecar

capture 原子保存原信件正文、发送者 CharacterId/名称快照及既有来源/目标，不新增 FrozenTask。真正 Start 前临时渲染任务，将该次任务的 UTF-8 长度/摘要加入现有 `StartQueuedMail` 的 Started + active route CAS 事务，确认提交成功才调用 transport。提交不确定遵循前述恢复规则。

旧 Driver、sidecar Inspect 和 operator recovery 用 task 全文验证同一工作。这些消费者必须共同改为使用实际提交时记录的 task 摘要/长度与 dispatch/thread/turn 等身份；Inspect 可以在远端读取到的实际任务上计算摘要，不能重新渲染本地原文冒充旧任务。只有 dispatchId 相同不足以证明内容相同。

首版不增加提交 ID：当前 C# transport tombstone 按 dispatchId 拒绝重复 Start，只有对应请求的严格 NotDispatched 才释放；已有 Requeue 同时清 active route。确证未发后同一个 dispatchId 可以重新渲染、重新 claim；Started/Accepted/OutcomeUnknown 不释放、不换承诺。requestId 仍只作 RPC 关联，不把 clientUserMessageId 宣称为远端 exactly-once 保证。

Inspect 传原 task 摘要/长度及 dispatch/thread/已接受 turn。cold 或跨连接 generation 的恢复必须读取唯一匹配 clientId 的 userMessage，验证单 text、空 text_elements、正确 thread/turn 和完整原文承诺；不能拼接多个 content 或抽出 body。

同 generation 的 live 路径还保留一种既有证据：本进程实际 Start 的完整任务承诺，经相关 RPC 响应绑定 Accepted turn，再消费同 thread/turn 的通知。app-server 可返回未包含 userMessage 的稀疏结果；此路径信任其请求/响应关联，证明相关提交与执行，**不声称已经回读远端正文**。凡观察到 userMessage 仍严格核对，矛盾则拒绝；无相关 Start、generation 丢失或 live 缓存不足时走既有 cold 核对，不增加重发权限。不能把全量历史回读新增为已关联 live 结果结算的先决条件。

两类证据均使用本次**完整任务**的严格 UTF-8 摘要。旧 live cache 的 UTF-16 摘要须与 durable 证据统一；Start、cold、live、operator 同步验收。

旧已发送任务的长度不能用新版 Start 大小上限否决：Inspect 不再传全文，仍受现有远端读取/分页边界约束；读取不足明确 unavailable，不能推成未发送。既有旧 schema 中 mail.Body 就是当时 Task，可按原字节取得旧承诺；不能用当前 renderer 包装它，也不补造当时发送者。C#/TS 同步升级为一个新的 Inspect 协议，不必长期保留双 wire。

这项原则约束 Galatea 及其可控 sidecar 的重复存储；Codex app-server 自己保存实际收到的会话历史属于远端执行事实，Galatea 无法通过瞬态渲染取消该外部持久化。Galatea 不以重读远端正文来恢复自己的原始业务输入，也不改写 Codex 私有 SQLite。

## 5. 投递证明、Note/recall 与资源预算

### 5.1 投递证明比较结构化事实

既有 Bound 的目标 exact base、message/lease/receipt 关联和目标 writer 门禁保留；新版 Bound 绑定的是将追加的**确切结构化 Observation**或其不可变引用，目标 Journal 追加相同内容，再用既有 exact base/当前轮次证据证明。

可以沿用旧证明框架比较稳定编码，或比较内容记录身份及确定的内容承诺；不能只查“目标出现过这个 messageId”就判 Delivered，也不能用重新渲染的 Markdown 比较。三个故障轨迹仍须成立：

1. capture 已提交、目标忙或进程退出：durable outbox 保留输入。
2. 目标 append 已完成、源 store 未 settle：从结构化 Journal 证明 append，settle 后不重发。
3. 上述窗口内有下一轮、abandon 或 undo：先对账或阻塞，不能先破坏证明窗口。

Delivered 仍表示目标已 durable append，与模型是否成功理解/完成无关。Renderer 失败可以阻止 LLM 请求，但不能把已 append 的内容变成未投递。

### 5.2 receipt、recall 和派生上下文

- CharacterMemory 的新回执保存成功事实、源 Action、pod、按 ordinal 排列的 MemoId 与当时 ExactText/不可变来源，绑定和 Applied 验证读取这些事实。旧 `notice_body` / `rendered_observation` 按原格式解释；旧 Pending 与新绑定的衔接见第 7 节，不将新包装改字段名后保存。
- 当前 Note 批量保存可能大于通知预算，原有全文→仅保存标识降级不能遗漏。用一个 receipt 专用内容选择函数，在首次请求计划确定前选择“全部确认保存的 IDs + 可展开的完整正文”；放不下时保留全部 IDs、零正文，不截断。结果进入既有内容计划，无 presentationMode 或通用选择框架。相同 Prepared 换 renderer 后放不下，只能报告未发送失败，不能再次删字段；Bound/Delivered 对账也不运行该选择函数。
- Codex reply lease 保存精确选中的 notices、各自来源及组合输入；claim/bind/settle 的原子范围与单 writer 保留，未知结果不重选一批回信。
- Dynamic Recall 明确保存 sourceId/不可变版本、当时 title、exactText 及已选顺序；若采用 gist 等内容也保存其当时值。当前 `GalateaMemoExactTextBodyRenderer` 生成的“标题/正文”串不能成为新版持久 Body。Title→OriginBarrier→RecallBarrier→预算→0..1 的既有选择语义保留；计划冻结后不按 renderer 重选候选或读取最新 Memo。
- Note DerivedInfo 的上下文读者直接读取结构化事实，在实际 LLM 调用时投影。`GalateaVisibleActionTextRenderer` 虽以 Renderer 命名，实际 Text block 保序选择、连接和 inline-think 排除属于**稳定语义提取**，不能与 md-json 样式一起替换。新 provenance 基于 Action/稳定语义视图，旧 fingerprint 按原算法；改变可见性、抽取指令或工具 schema 要按领域合同变更处理。
- Memo recall query、DerivedInfo 和 Recap 等辅助请求保留其**实际选中块**的来源、业务时间与种类含义，不要求复制完整 Observation。只传 `PlayerText` 或 `notice.Body` 不能算来源已接通；辅助请求未携带主线 SystemPromptSetup 时，其指令仍须按同一语义合同解释这些字段，不由局部 renderer 另造解释。
- RecapGrid 的 ExactContextInputs / ContextSnapshot 保存 cell 内容及 carrier/来源等语义字段，消息包装只在每次实际请求中生成；不能仍将展开后的 ObservationMessage/SystemPromptFragment 当新版内容权威。
- 角色设定及 Recap Maintainer 指令的**内容变化**仍遵循现有资产定义/target/构建规则；只改变 md-json 的围栏/布局不应改变业务 target digest 或使历史 cell 失效。当前绑定实际文本的 identity 消费者须逐一分清这两类变化。

当前 `FamilyInputRenderingProtocol` 进入 Family digest，`SemanticHeading` 进入 Maintainer 定义。新版保留字段含义、历史范围、可见性、carrier、顺序和 OutputProtocol 等语义协议；heading 文字是内容，其 `#` / fence 装饰是瞬态表示。首次拆开这些混合定义可能需要新资产采用；以后纯风格变化不改变 digest。Recap 的 `RuntimeRenderer.ProjectHistory` 必须显式支持 structured Observation，不能只接通主线 renderer。

Recap `PreparedRecapWork`、`PreparedFamily` 与 family cache 当前是内存对象；DerivedInfo 的 durable Prepared 保存已验证产物。它们不新增主线式 attempt WAL，只在各自 invoker 前投影/检查请求，并保留原有 cell/result 提交与失败政策。历史查询不重新做辅助调用；尚未完成的辅助工作仍由其既有 reconciliation 推进。

**MemoPod 自身的缓存也服从此边界。** 基线实现的 Open / Freeze 会调用 prompt renderer，Recall 又把缓存对象身份当作 Frozen epoch。新版 Open / Freeze 只验证、冻结或发布机读 document；到 Recall 请求时才投影并检查呈现限额，缓存缺失或渲染失败不影响健康 document 的打开和 Note 保存。保留既有 Publish / CommitIndeterminate 合同；Recall 在 await 前后校验同一冻结内容版本，不能以渲染缓存对象充当内容版本。清缓存不使版本失效，真正编辑仍使旧 Recall 结果失效。`FrozenPromptSha256` 只表示本次投影摘要，不替代机读 Pod 状态身份；无需新增持久 schema、epoch ID 或辅助调用 WAL。

这里的 Frozen epoch 是本进程的冻结生命周期，不能只用内容 hash 代表：`ResumeEditing → Freeze` 即使内容未改也开启新周期，使旧在途 Recall 失效。每次冻结建立的不可变快照对象即可承载该身份；清缓存、换 renderer 不更换它，也不将此身份持久化。

### 5.3 存储与请求预算分别核对

存储上限根据实际机读记录、正文和持久引用计算；不通过当前 renderer 估算既有 outbox、receipt 或 store 的大小。renderer 升级不能使同一结构化 store 突然不能 strict reopen。

实际请求的字节/token 上限在临时组装后、外部发送前核对，包括完整包装。扩大的包装可能使某次请求暂时不可发送，报告该失败并保留输入，不截断原文、不删除邮件、不把它误记为已发送。渲染缓存失效不能改变 admission/恢复权限。

Codex 临时包装过长时保持未发送的 Queued 工作，报告局部投影失败，不走当前 `FailQueuedMailPreflight` 将原文当非法任务终结并清 Body 的分支。稳定原始内容违规仍遵循原业务拒绝政策；本地投影失败沿现有轮询/失败处理节奏，不新增 BlockedMail 状态、通用预算框架或外部重试权限。

## 6. 当前消费者与施工边界

| 消费者 | 当前证据入口 | 必须改到的边界 |
|:--|:--|:--|
| Galatea fresh send / recent / undo / extraction | [Services](../../prototypes/Galatea/GalateaServices.cs)、[Observation](../../prototypes/Galatea/PlayerTurnObservation.cs) | append 结构化事实，查询直接读字段，LLM 前投影；input normalizer、extractor 的源证据不被显示文本替换。 |
| SessionJournal raw / request / recovery | [Engine](../../prototypes/SessionJournal/SessionJournalEngine.cs)、[reconstructor](../../prototypes/SessionJournal/SessionPreparedRequestReconstructor.cs)、[coherent recipe](../../prototypes/SessionJournal/SessionCoherentRequestRecipe.cs) | typed storage tag、语义输入计划、瞬态 request、版本化的 attempt 合同。 |
| setup / audit / runtime recovery | [DesiredSetup](../../prototypes/SessionJournal/SessionJournalEngine.DesiredSetup.cs)、[AuditScanner](../../prototypes/SessionJournal/SessionJournalAuditScanner.cs)、[RuntimeRecovery](../../prototypes/SessionJournal/SessionJournalEngine.RuntimeRecovery.cs) | 语义 setup 比较，新版 verifier 不调 renderer；保留 raw/工具/引用/尝试归属验证，旧版本权限不扩大。 |
| exact append proof | [CompletedTurns](../../prototypes/SessionJournal/SessionJournalEngine.CompletedTurns.cs)、[角色信 delivery](../../prototypes/Galatea/GalateaCharacterMailDelivery.cs) | 新输入证明使用稳定机读内容，仍保留原 exact base 与门禁。 |
| delegate Start / Inspect / operator | [Driver](../../prototypes/Galatea/GalateaDurableDelegationDriver.cs)、[Recovery](../../prototypes/Galatea/GalateaDelegationOperatorRecovery.cs)、[dispatch inspection](../../local-codex-mcp/src/codex/dispatch-inspection.ts)、[live observations](../../local-codex-mcp/src/codex/live-turn-observations.ts) | 摘要先持久化再 Start，cold/live/恢复严格使用同一字节合同与结构校验。 |
| Note receipt / DerivedInfo / recall | [ReceiptDelivery](../../prototypes/Galatea/CharacterMemory/CharacterMemorySqliteStore.ReceiptDelivery.cs)、[materializer](../../prototypes/Galatea/CharacterMemory/CharacterNoteDerivedInfoContextMaterializer.cs) | 语义回执、结构化上下文及独立投影；删除新写入的渲染通知权威。 |
| MemoPod document / Frozen / recall cache | [Open](../../prototypes/MemoPod/MemoPod.cs)、[Freeze](../../prototypes/MemoPod/Lifecycle/MemoPodLifecycle.cs)、[Recall](../../prototypes/MemoPod/Recall/MemoPodRecall.cs) | Open/Freeze 不依赖 prompt；请求前投影，缓存与冻结内容版本分开，保留发布和并发结果校验。 |
| recap projection / operator CLI | [context materializer](../../prototypes/SessionJournal.RecapGrid/Getter/RecapGridContextMaterializer.cs)、[RuntimeRenderer](../../prototypes/SessionJournal.RecapGrid/Runtime/RuntimeRenderer.cs)、[CLI catalog](../../prototypes/SessionJournal.Cli/RecapGridOperatorAssetCatalog.cs) | carrier 与语义输入合同分离，实际摘要请求消费新内容种类；不新建 recap attempt WAL。 |
| 调用日志与缓存 | [Galatea logging](../../prototypes/Galatea/GalateaCompletionLogging.cs)、[CLI recap logging](../../prototypes/SessionJournal.Cli/RecapGridCompletionLogging.cs) | 两个真实适配点都不再存新渲染全文；sidecar 已只记 metadata 且 cache 是内存 Map，无需虚构缓存迁移。 |

本期不重构无关存储引擎，也不改所有 provider 的 native wire 格式。接入点跨 SessionJournal、Galatea、Galatea.RecapGrid、MemoPod、CLI 与 sidecar，不能将整个目标标成纯 Galatea DTO 重命名。

## 7. 迁移与纵向验收

### 最小实施切片

1. **结构化动作贯穿一条主线**：与主方案身份配置一起完成 schema/tag → append → 查询/undo → md-json request → 明确未发送/已发送的 attempt。用 fake provider 验证，不能将“绕过输入投影器，直接发送数据库中的 JSON 字符串”算作接入完成；首版须证明 md-json 请求通路实际生效。未来显式选择 JSON 作为另一种请求表示并不违背存储/渲染分离，本期不为此新增 renderer。
2. **同一内容通路覆盖全部消费者**：邮件、reply lease、Note、recall、recap、Codex task 与 sidecar 核对，以及 zero Player 后台路径。收口后不存在新版“先渲染再存储”的旁路。
3. **旧状态和故障闭环**：按真实 schema 边界提供离线升级与旧读取路径，做 crash/reopen、格式切换、CLI 和引用工程验证；然后才形成实例迁移步骤。

旧日志和已发送/Bound 请求不原地重写。已知旧 envelope 可以投影为结构化内存视图；来源缺失标记为旧记录未提供，不查当前账号补造作者。无法可靠解码的原文保留 legacy content，不猜测字段。正常新写入只使用新内容合同；旧 reader 是已有数据的消费边界，不是长期双写。

**既存内容的来源格式与本次 Observation 的绑定格式分别判别。** 旧 Pending receipt/reply 可以作为显式 legacy 块进入新 structured Observation，保留旧冻结正文及已有来源；这不是生成并持久化新的渲染全文。已 Bound 的旧输入仍按原证明结算，不能为了统一格式改写它。

SQLite owner/dispatch ID 保持旧值，但本文涉及 receipt、lease、outbox 和 Prepared 的真实格式变化，不能再承诺“CharacterMemory 完全不用迁移”。各域只为新增机读内容和发送事实升级，保持原业务身份、原子事务与恢复状态。不增加全库改名、ID 映射或通用迁移框架。

| 验收场景 | 通过标准 |
|:--|:--|
| 同一持久输入使用两个 renderer，清空所有渲染缓存 | 两次投影可不同；原机读字节、来源、UI/undo 结果与 store reopen 相同。 |
| 禁用 LLM 输入投影器后运行 recent / undo、纯机读 CLI 与网页展示 | 已知 schema 仍可查询、审计和独立显示；只有实际发起 LLM 请求的路径因缺少投影器而失败。 |
| 原文含 CR/LF、空白、围栏、伪元数据、深层内容 | JSON 读回和 md-json 局部往返保真；选择路径不成为持久字段。 |
| 未知 schema、重复键、无效 Unicode、Pointer 误选缺失或非字符串正文 | 内容接纳与投影错误分层报告；不猜字段、不清已接纳正文、不调用外部服务。 |
| Player 动作附带回信、Note、recall，及 HTTP from 自称 Codex | 各块来源正确；动作语义不变；undo 只恢复动作原文。 |
| Prepared 后、Started 前重启并换 renderer | 同一语义 Prepared，成功时仅新增首次 Started；失败时 Started/外部调用数为零，无新 Prepared/结束事件/重复召回。 |
| Started 提交前/后返回异常 | 本次不调用 provider；reopen 按实际 Prepared/Started 分类，不凭异常覆盖证据。 |
| 显式授权 uncertain 重试并换 renderer | 同一内容计划下两个 Started 各有承诺，旧未知证据保留，不伪记 NotDispatched。 |
| Started/OutcomeUnknown 后重启并换 renderer | 用原发送证据核对；没有 NotDispatched 证明或既有显式授权就不重发。 |
| Codex task Start、Inspect、operator recovery 跨 renderer 版本 | Inspect 使用原任务承诺，外部任务身份不漂移；Start 的证据先于外部效果持久化。 |
| live/cold 使用不同缓存、旧任务超新 Start 限额、异常 userMessage 形状 | 原 UTF-8 承诺核对一致；旧任务仍可 Inspect；多消息/多 content/附件不能仅凭匹配正文被接受。 |
| 稀疏 Start 响应、同 generation 通知与冷重启 | 本进程相关 Start 可建立 live 关联；迟到矛盾 userMessage 被拒绝；清缓存后旧关联不复活，cold 必须回读原 userMessage。 |
| 目标 append 后、sender settle 前 crash，随后 undo 抢先 | 结构化 exact proof 先结算，至多一次投递、无证据窗口丢失。 |
| 旧 Prepared / Bound / Applied receipt / reply lease | 旧版本正常读回与恢复；新 renderer 不参与旧 canonical 校验。 |
| 旧 Pending receipt/reply 进入新 structured Bound，append 后 crash | 原文和已知来源保留；按新绑定的机读内容对账，不补造身份，不重写旧 Bound。 |
| 近容量 store 换成更冗长 renderer | strict reopen 不受影响；超长请求在发送前失败，保留输入与未发送事实。 |
| 新生成的 receipt/recap/辅助 LLM 请求 | 持久文件只有机读内容与调用事实，不出现 renderer 生成的包装、模板快照或重复全文日志。 |
| 大 Note batch、Memo 修改、VisibleAction 样式/语义变更 | IDs-only 选择在计划前完成；重渲染不再删字段；recall 使用当时 title/exactText，旧 provenance 不随风格漂移。 |
| 禁用 renderer 后审计、修改 setup 布局后重开、篡改引用 | audit/reopen/DesiredSetup 不依赖 style；真实内容、工具序列或尝试归属篡改被拒绝。 |
| 辅助 query / Recap 未携带主线 setup，历史时间与 Memo 已变化 | 选中块的原来源、时间、类型含义和版本仍正确；源不可用明确失败，不以当前事实替代。 |
| 禁用 renderer 后 MemoPod Open/Freeze，Recall 清缓存/编辑交错 | 机读发布和打开正常；Recall 投影失败零调用；清缓存不改 Pod 身份，ResumeEditing 后即使原样 Freeze 也使旧结果失效。 |
| 零 Player 两角色，公共 CLI、新/旧 recap target | 后台能运行；语义资产变更按既有构建采用；纯包装变更不重建 cell。 |

## 8. 审查结论

前轮三位审阅者分别从 Semantic defender（SessionJournal 恢复）、Minimal architect（各内容消费者）、Demand skeptic（transport/md-json）独立核对相同方案和账本，再交叉质询。主线程独立核查并修订，审阅者未编辑文件。两轮后收敛，没有需要追加用户决策的争议。

| 议题 | 裁决 | 最小结果 / 具体失败依据 |
|:--|:--|:--|
| FrozenTask、持久渲染 Observation/receipt/context | delete | 由用户 U1 明确替代；输入存内容，核对存调用事实，不把渲染全文改名藏入 JSON。 |
| 未发送风格变化的 supersede/新 Prepared/新提交 ID | simplify | Prepared 只存计划，每个现有 Started/claim 带本次承诺；无 Started 就无需要结束的调用尝试。 |
| 发送记录的提交不确定窗口 | keep | 先不开外部调用，再按重开状态分类；不能凭异常抹掉已发布 Started。 |
| SystemPromptSetup、audit、召回内层 Body、CLI 日志 | keep | 都有现存消费者，已补入施工地图；只改主线 Observation 会留下真实渲染落盘旁路。 |
| 回执 full/IDs 由 renderer 任意决定 | simplify | 移到一个现有请求规划阶段的窄内容选择；冻结后风格不再删字段，保住大 Note 的确认能力。 |
| VisibleAction 筛选、Family 的历史范围/输出合同 | keep | 内容可见性和归因属于语义；随风格变更会破坏 provenance 或 target，不能一并去掉身份。 |
| Recap 的内存 Prepared 统一升级为调用 WAL | defer | 无真实持久恢复消费者；保留各域现有提交机制，不因同名 Prepared 扩平台。 |
| UTF-8 task 摘要与 live UTF-16 摘要混用 | simplify | 明确边界并统一 cold/live/operator；保留 userMessage 结构和关联验证。 |
| 通用 JSON Pointer 规则、反射选择、缓存迁移框架 | defer | MdJson.Write 加调用侧实际 shape 枚举即可；未来独立消费者出现再做。 |

交叉质询明确修订了三处初稿：未发送时无需“结束旧尝试”；sidecar 不需要新增提交 ID；回执全文/IDs 是内容选择，不能让 renderer 任意变化。主线的实际请求证据也已明确为 ICompletionClient canonical 输入，未扩成全 provider HTTP wire 审计。

最终复用两类既有持久边界（SessionJournal Started、delegation Started/route claim），新增的是其内容/证据 schema 接入；不新增 supersede 状态、提交身份、统一辅助调用 WAL 或渲染版本存储。真实成本仍包括跨域内容消费者改造、旧格式读取/升级与新语义 recap 资产采用，不能据此称为只换一个 renderer。

### 本次补充复核

本次仍由三位独立审阅者按上述三个角色检查，再交叉质询；主线程核对代码后补齐以下结论。审查只完善设计，不把工作区实现或未运行验收当作已通过证据。

| 议题 | 裁决 | 收敛结果 |
|:--|:--|:--|
| MemoPod 打开/保存必须先渲染；缓存对象兼任 epoch | simplify | 推迟到 Recall 请求；保留内容版本和发布合同，缓存不再决定数据有效性。 |
| 辅助请求只带正文，含义仅藏在主线包装中 | keep | 保留所选块的来源和语义解释；不强制复制完整输入。 |
| 旧 receipt 内容必须绑定旧 Observation 格式 | simplify | 来源格式与绑定格式独立；旧原文可进入新容器，既存 Bound 不改写。 |
| 支持域、源版本可用性与失败阶段 | keep | 接纳前验机读数据，投影前取原版本，发送前处理渲染失败；不增加通用校验或引用平台。 |
| Pointer 块顺序、md-json Read 的权威 | clarify | 数组/关联表达业务顺序；排版不另成权威，Read 不证明完整性或投递。 |

质询修正了两个容易过度约束的提议：不禁止保持关联的正文块物理重排；不将不可变引用要求扩大为无限保留。没有新增权限模型、全局 ID、渲染版本存储或辅助调用 WAL。

实施复核还纠正了前文对 live 证据的过强概括：既有同 generation 的相关 Start RPC 可处理稀疏响应，而 cold 恢复要求回读唯一 userMessage。第 4.4 节分别列明两条证据链，保留原信任和恢复边界，不把“已经回读远端正文”强加为所有 live 结果的承诺。

2026-09-16 文档复核由需求怀疑者、最小架构师、语义守护者独立检查，再交叉质询。三者未发现需要新增机制的遗漏；本次补上第 2 节职责表，并将 CLI / 历史查询的限制明确为不依赖 **LLM 输入投影器**，保留网页的独立显示能力。另明确诊断日志没有派发权威，以及未来显式选择 JSON 表示仍可遵守分离原则。上述结论只证明设计收敛，不替代实施验收。
