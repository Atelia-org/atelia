# 角色状态驱动的连接选择

状态（2026-09-25）：**第一、第二阶段均已实现。** 第二阶段使用 Observation v4 与 SystemInstruction v3，落实名称优先、低频连接 ID 和进程内投递确认。实现与验证记录见文末。

第二阶段施工前源码基线为 `52fdb6e6`（`feat(galatea): drive connections from character state`）。第一阶段结束时未由实施助手重启服务；随后用户已提供实际 GM 输出反馈，但本次没有核验运行进程、部署版本或具体请求，不再将“未重启”当作当前服务状态。

前面的机制章节描述第一阶段现状；第二阶段章节明确替换其中的**主线呈现方式和最近切换记录的投递方式**，其余识别、选路与恢复边界沿用。

## 目标与取舍

每个 Character 的 `connectionOptions` 定义 `connectionId`、`name`、`trigger`。trigger 是回合结束时的状态条件，例如穿着裙装、穿着裤装、戴着眼镜或未佩戴眼镜。辅助模型从成功完成的终结 Action 提取唯一匹配，宿主设置进程内连接选择。识别失败后，后续回合仍可凭持续的状态摘要校正；不依赖一次性的换装动作或 toggle 事件。

新接纳回合的连接优先级保持：`diagnosticConnectionId ?? runtimeConnectionOverrideId ?? defaultConnectionId`。选择在接纳时固定；改变 override 不改变已接受回合或 Prepared 的恢复身份。诊断调用产生的正常剧情参与状态识别，但诊断 ID 不直接写入 override。

## 状态与识别

- 主线协议要求 `[状态摘要]` 持续记录与 trigger 有关的已成立事实，包括未改变的状态；未知时不得补造。
- 使用专用 `CharacterConnectionStateExtractor` 复用 `TextExtractor` / `ArtifactToolWrapper`。输入是本角色身份、同一份选项快照和终结 Action 可见文本；工具产物携带 connectionId 和有界原文证据。
- 只有当前角色已经成立的最终状态可匹配；计划、引用、回忆、其他角色、未成功的动作均不构成匹配。没提眼镜不等于未佩戴。摘要与叙事明显冲突或多条件同时成立时放弃切换。
- 提取器提供两个互斥工具：`emit_character_connection_state` 提交 connectionId/evidence，`emit_character_connection_state_unknown` 用空对象明确报告无匹配、歧义或冲突。整个结果最多一个产物，零工具调用也保持原值。重复匹配同一 ID 不产生新切换记录；多个产物、越权 ID、空 trigger、无效证据或工具输出错误均使本次识别失效，不能部分应用。
- `name` 用于展示语义，`trigger` 用于匹配，`connectionId` 是唯一精确标识。空 trigger 不参与自动识别，仍可用于诊断选择。
- runtime 连接不是故事状态的证据。不能因默认/诊断连接而反推衣着、物品或人物意图。

## 执行与生命周期

成功完成回合且终结 Action 已落盘后，在持有 Character `TurnLock` 时执行识别，并在下一回合接纳前应用结果。识别独立于 mail/note 的 durable settlement，不引入 durable 提取计划。覆盖人工消息、人工邮件、内部角色信、DelegateReply、心跳和恢复后成功完成的回合；失败或停止且没有成功终结 Action 的回合不识别。

识别使用整体时限（首版 30 秒，覆盖辅助客户端重试），失败/超时保留原值并记录 content-free 诊断，不把已完成剧情变成失败。取消遵守宿主生命周期；不能将仍在运行的提取任务遗弃在锁外。工具回调只收集产物，验证全部结果后由宿主原子替换状态。

override 和最近切换记录只存在于进程内；冷启动初始为空，新回合在尚未识别出状态时使用 default，不扫描历史或重放动作。已有回合恢复完成后的识别也可建立选择。恢复匹配可能需要多回合，不能承诺只偏差一回合。撤销最新回合成功后清除本角色 override 与最近切换记录；后续剧情重新建立选择。

## Observation 连接快照（第一阶段现状）

每个新接纳的主线输入携带冻结的 `connectionState`，包括 `runtimeOverrideConnectionId`、`effectiveConnectionId`、`turnConnectionId` 及可空 `lastChange`。前三者分别是内存选择、常规有效选择和该回合实际绑定连接。快照覆盖全部触发类型，包括 inbound-mail。

实际常规有效连接变化时形成进程内事件记录：来源 Action 地址、旧/新 connectionId、选项名、匹配证据。作为 `lastChange` 随快照重复呈现，明确表示最近一次历史变化，不是新指令，不承诺 exactly-once 通知。null 到 default 的显式赋值不算有效连接变化。

无需 outbox、消费确认或额外数据库。快照随已有 Observation 正常落盘；它是历史输入证据，不是恢复 override 的权威。已有输入的恢复必须重用其快照，不能读当前内存改写。新字段使用新 Observation schema，旧输入继续按原 schema 投影；内部信证明、回执和输入限额等消费者须同步核对。

`turnConnectionId` 精确描述新回合接纳时绑定的连接。已有恢复 API 对兼容的 NewRequestRequired 仍可能允许诊断连接；这种恢复不改写历史快照，快照不能用来证明每次恢复 transport 的连接身份。Frozen request 的目标约束保持原样。

## 提示词与配置（第一阶段现状）

新增 binding `galatea.character-connection-state-extractor`，字符串指向独立辅助连接，null 禁用。绑定启用且角色存在非空 trigger 时才启用该角色识别并追加机制说明。

新增 embedded appendix `prompt/trpg-character-connection-state-appendix-zh-cn.md`，沿用邮件/note 的固定协议 source。角色 `connectionOptions` 保存为 typed Setup 的 bindings 数据，经现有 md-json 投影给主线模型；辅助识别使用相同选项。禁止维护第二份人工选项表或在恢复时从当前配置替换历史 Setup。扩展 SystemInstruction schema，保留旧 Setup 精确读取；动态连接快照放在 Observation，不反复改写 system setup。

根 config 的 connectionOptions 形状保持 V14；Completion catalog V3 增加一个 Galatea 必需的可空 binding。首次 scaffold 默认禁用；本地现有实例按授权加入辅助连接 binding，并保留备份。修改本地配置不表示服务已重启或真实会话完成升级。

## 第一阶段验收

1. 工具调用结构、唯一性、精确角色 allowlist、证据校验；正文不当作产物。
2. 重复匹配幂等；未知、冲突、超时、失败保留现状；两个角色互不影响。
3. 人工、自动、内部信和恢复完成共用完成后识别；下一新回合生效；诊断优先且不直接污染 override。
4. 快照在接纳时固定；所有触发携带；历史恢复不重算；冷启动及撤销清空进程状态。
5. 主线 prompt 的 capability gating、角色配置绑定、特殊字符和旧 Setup 投影；默认模板与配置严格校验。
6. provider-free 定向与服务端回归测试；如执行 live 提取 canary，应使用合成文本和隔离客户端，不操作现有角色会话。

## 源码入口

- [TextExtractor](../../prototypes/Galatea/TextExtractor.cs)
- [状态提取器](../../prototypes/Galatea/CharacterConnectionStateExtractor.cs)
- [进程内选择、快照与完成后识别](../../prototypes/Galatea/GalateaHostService.ConnectionState.cs)
- [GalateaHostService / 配置装配](../../prototypes/Galatea/GalateaServices.cs)
- [Completion owner](../../prototypes/Galatea/GalateaCompletionOwner.cs)
- [SystemInstruction](../../prototypes/Galatea/GalateaSystemInstructionContent.cs)
- [Observation](../../prototypes/Galatea/GalateaObservationContent.cs)
- [主线协议 source](prompt/README.md)

## 第二阶段：名称优先与低频 ID（已实施）

### 背景与已确认决策

用户观察到 GM 在 Action 结尾自行输出：

> **连接观察：**本轮接纳快照的有效连接与实际连接均为gpt-6-astra-codex；后续按未佩戴眼镜状态选取的结果尚待观察

这符合先接纳、生成 Action、再提取状态的时间顺序，但暴露了呈现问题：角色的注意力被底层模型标识吸引。当前协议没有要求输出“连接观察”栏目，该栏目由 GM 自行生成。角色身份、职责与经历应保持连续，模型版本不应被暗示为人格、情绪、认真程度或故事事实。

用户认为功能定位比具体 model-id 持久；名称也有心理暗示，因此不只把 ID 换成形容词，还需要一段简短的固定解释。用户会与角色本人商量最终名称；本轮不替用户改写实际 name/trigger。

确认的暴露规则：

| 位置 | GM 可见信息 |
|:--|:--|
| System prompt | 角色的全部可选配置；以 name/trigger 为主要说明，同时提供对应 connectionId；加入简短的名称含义与运行机制说明 |
| 普通新回合 Observation | 常规运行配置的 name；无诊断覆盖时不重复列出两个相同配置 |
| 存在临时诊断覆盖的回合 | 常规配置 name，以及本回合临时配置 name；不把临时值写入常规 override |
| 实际常规连接变化后的首次新 Observation | 前后配置的 name 和 connectionId，以及状态依据；后续新回合只展示名称 |

这里的“低频”指**低频新增技术标识**，不是保证每个实际 HTTP 请求都不含旧 ID：固定 system prompt 仍随请求发送，历史中的切换说明也仍然存在。不要为消除旧 ID 而裁剪或重写历史，这会改变恢复输入和可复用前缀。

### 文本模板目标

以下是批准方向的可施工文本草案，允许实施时作必要的简洁调整，不是必须逐字匹配的合同。

固定系统说明：

```text
以下是可选的运行配置。名称用于标识配置，不定义角色的情绪、行为或思考深度。runtime 根据回合结束时已成立的状态选择后续配置；无法确定时保持原选择。请在状态摘要中保留相关事实，无需例行报告配置信息。
```

系统选项示例（实际数据必须取自本次 Setup 的角色选项快照）：

```text
深入推演：戴着眼镜时采用。连接：gpt-6-astra-codex。
日常生活：未佩戴眼镜时采用。连接：gpt-6-sol-codex。
```

普通 Observation：

```text
当前运行配置：日常生活。
```

切换后的首次新 Observation：

```text
当前运行配置：日常生活。
运行配置已调整：深入推演（gpt-6-astra-codex）→ 日常生活（gpt-6-sol-codex）。
依据：上一回合结束时未佩戴眼镜。
```

临时诊断回合：

```text
常规运行配置：日常生活。
本回合临时使用：深入推演。
```

切换通知与诊断覆盖可以同时存在：通知说明**常规选择**发生的变化，另行说明该回合的临时配置；不能把诊断实际连接说成常规切换目标。

如果确实发生“名称相同、connectionId 不同”的常规连接切换，使用“连接已更新，配置名称不变”之类的表述，并附新旧 ID，避免暗示功能定位变化。这里只描述已观察到的连接变化，不引入跨重启配置 diff、模型升级探测或新版本查询服务。相同 connectionId 背后替换 modelId 不在当前状态提取器的检测范围。

### 第一阶段实际渲染与差距

本次使用现有 compiled projector 生成过真实 md-json 投影，输入为当前选项加合成回合，没有打开角色会话。临时示例位于 `/mnt/wsl/fast/tmp/galatea-render-examples-6tqt6bly/examples.md`；此路径可能消失，以下差距说明才是长期备忘。

当前 system 的 `bindings.connectionOptions` 原样含 `connectionId/name/trigger`。当前 Observation 的主体是：

```json
"connectionState": {
  "runtimeOverrideConnectionId": "gpt-6-sol-codex",
  "effectiveConnectionId": "gpt-6-sol-codex",
  "turnConnectionId": "gpt-6-sol-codex",
  "lastChange": {
    "sourceActionAddress": "ej1:00000000000000010000000100000000",
    "previousConnectionId": "gpt-6-astra-codex",
    "connectionId": "gpt-6-sol-codex",
    "name": "日常生活",
    "evidence": "/connectionState/lastChange/evidence"
  }
}
```

`evidence` 由 md-json 放在独立的文本块中，其余 ID 仍直接出现在 JSON structure。name 只在最近切换记录中出现，常规与实际配置没有各自的名称；lastChange 又会每回合重复出现。因此，只改 appendix 或在 JSON 后追加名称说明都不够：必须处理**整个模型可见投影**，不能一边显示名称、一边仍把所有 ID 原样输出。

### 数据快照与纯投影边界

以下是建议实现方向；具体类型和字段名可根据现场代码调整：

1. 保留精确 connectionId 作为内部选路、验证和诊断依据；name 是显示数据，不是唯一键。辅助状态提取器继续接收精确 ID 并按 ID 返回目标，不受主线低频暴露规则限制。
2. 在新回合接纳时同时冻结常规配置名称、本回合配置名称、必要的诊断覆盖区别，以及本回合是否携带切换说明。切换记录也需要冻结**前后两侧名称**，当前 `GalateaConnectionStateChange` 只有新名称，尚不足够。
3. 名称须与 ID 来自同一角色选项快照。投影器不能打开 config.json、访问 host 内存或查当前选项补名称；历史数据投影不能因配置改名而漂移。
4. 建议采用新 Observation schema（预期 v4）保存新的冻结数据。普通回合机器记录可以保留精确 ID，但该版本的 GM 投影只输出名称；携带切换说明时才把新旧 ID 与名称一起投影。切换事件可作为可空字段，避免保留一个每回合必须输出的 lastChange。是否复用字段名在实施时决定。
5. `GalateaObservationInputProjector` 当前是 `MdJsonSerializer.Write(input.JsonValue, ExternalStringPaths(...))`，近乎原样输出。新增名称优先投影后，应先从已验证的机器内容构造专用模型可见值，再交给 md-json。不得修改原始 `SessionInputContent`、存储、exact proof、Undo、审计数据。
6. 因为新投影可能省略机器字段，不能再要求“投影文本反解析后与全部原始机器 JSON 完全相等”；该等式仅继续适用于旧 schema。新测试应分别证明原始输入不变、语义显示正确、同一冻结输入重复投影结果一致。
7. SystemInstruction 当前 v2 的固定 source 与全部配置映射可以继续承载低频 ID。若显示结构/投影语义改变，实施者决定是否引入新 system schema（预期 v3）；不能通过修改旧版本 projector 重解释已有 Setup。仅固定 appendix source 改动也会在下次新回合形成新的 Setup 内容。
8. 默认配置也能映射名称，不需要等第一次 override 才有名字。当前 config 合同允许 name 为空、重复或含换行，不能擅自升级为身份键或要求唯一；实现中要处理这些输入。建议空名显示“未命名配置”，准确 ID 留在 system/切换说明，不以每轮回退 ID 的方式悄悄破坏低频规则。这个空名显示细节是建议，可现场细化。

### 切换说明的一次新增与失败恢复

新设计替换第一阶段“不需要消费确认”的简化：需要**进程内的投递确认**，但仍不新增数据库、持久化 outbox 或 override 文件。

- 常规有效连接真正变化时，形成一个带身份的 pending change；可用进程内递增 revision 或来源 Action 加新旧 ID 标识，避免旧回合确认误清除新事件。重复匹配相同 ID、null 到 default 的显式赋值都不制造事件。
- 新回合接纳时在 `TurnLock` 内将尚未投递的 change 冻结到该回合。只冻结，不立即标为已投递；HTTP 202、live turn 创建、SSE 开始、provider 返回成功都不是输入落盘证明。
- 真正调用 `Engine.SendAsync` 之前，最终 prompted Observation 已包含 recall、note receipt、连接状态等全部字段。此时可在进程内保存用于确认的 exact base head、最终 `SessionInputContent` 和所携带 change 身份。
- 确认边界是**该 exact Observation 已落盘**。可复用现有 `ProveExpectedObservationTurnAtSelectedHead` 读证明：`NotAppended` 保留 pending 供下一次新输入；`InProgress`、`Terminal`、`Terminated` 表示已 append，可确认对应事件。未知/损坏证明不能静默当作成功，继续遵守现有 writer/recovery 失败边界。
- 确认应覆盖正常完成、generation 失败、用户停止、取消等出口，且在锁内执行。`GalateaFreshSendLifecycleGate.PrepareAsync` 是 pre-observation 阶段，不能直接把其成功当作已经 append。
- 最简单的接入位置可在 fresh Send 的 finally/统一 settlement 中，用 exact 证据确认。注意当前 `RunTurnAsync` 会在完成后先运行状态提取，再结算一些 durable deliveries；不能用一个无身份的 `pending=false` 在此误清掉新 Action 刚形成的下一次切换。先结算本次携带事件，或按被冻结的 revision 条件确认。
- 已落盘但 generation 失败后，恢复会重用原 Observation，因而可能再次把同一说明发送给 provider。这不违反“仅首次新 Observation 新增一次”；不承诺物理请求 exactly-once，也不为去重改写历史输入。
- 进程重启后 override、pending、已投递标记都消失，不从历史重建。历史中的说明继续只是历史事实；新回合如实展示新的当前配置。成功 Undo 清空同角色的这些进程状态；无须建立事件重放体系。

参考 [GalateaNoteReceiptDelivery](../../prototypes/Galatea/GalateaNoteReceiptDelivery.cs) 的 exact append 证明方式，但只借用边界和读证明，不照搬 CharacterMemory SQLite outbox。省去投递标记的替代方案会导致 ID 重复暴露，不符合已确认的新目标。

### 缓存与身份注意事项

- 角色自动切换不能把“当前选项”写进 system prompt；固定机制与全部映射留在 Setup，动态信息留在新 Observation。
- [ReconcileDesiredSetup](../../prototypes/SessionJournal/SessionJournalEngine.DesiredSetup.cs) 分别比较 runtime 的 model/surface 和 system 内容。换连接可能追加 RuntimeConfigSetup，但不因当前选择而重写 SystemPromptSetup。
- 首次启用新模板或用户修改 name/trigger/映射，会有一次真实 system 内容变化；不是每次角色切换都变。
- 稳定 system 内容不等于跨模型共享服务端缓存，也不能保证 cache hit。不要在交接中把这两件事混为一谈。
- “connectionId”与provider的“modelId”是不同字段；当前配置经常将 connectionId 命名为模型式字符串，这不是强制要求。本轮不批量改名现有连接，不引入型号生命周期管理。

### 实施定位与工作顺序

| 范围 | 入口与需要做的事 |
|:--|:--|
| 内存状态与冻结 | [GalateaHostService.ConnectionState.cs](../../prototypes/Galatea/GalateaHostService.ConnectionState.cs)：`RuntimeConnectionSelection`、`CaptureConnectionState`、`FreezeConnectionState`、完成后提取；补名称、pending 身份及确认 |
| 状态 DTO | [GalateaConnectionStateSnapshot.cs](../../prototypes/Galatea/GalateaConnectionStateSnapshot.cs)、[GalateaTurnOptions.cs](../../prototypes/Galatea/GalateaTurnOptions.cs)：不可变回合快照和切换两侧信息 |
| 接纳及输入 append | [GalateaServices.cs](../../prototypes/Galatea/GalateaServices.cs)：四类 Start 方法、`RunRecapGridFreshSendAsync`、`RunTurnAsync`、停止/失败结算及 `PrepareAndCommitPopLatestTurn` |
| 机器 Observation | [GalateaObservationContent.cs](../../prototypes/Galatea/GalateaObservationContent.cs)、[GalateaObservationSchema.cs](../../prototypes/Galatea.Input/GalateaObservationSchema.cs)：新版本闭合字段、名称/证据限额、历史版本继续读取 |
| 最终可见文本 | [GalateaObservationInputProjector.cs](../../prototypes/Galatea.Input/GalateaObservationInputProjector.cs)：按冻结输入输出名称优先文本；普通新输入的结构和正文都不泄漏 ID |
| 主线机制说明 | [appendix](prompt/trpg-character-connection-state-appendix-zh-cn.md)、[SystemInstruction](../../prototypes/Galatea/GalateaSystemInstructionContent.cs)、[Composer](../../prototypes/Galatea/GalateaSystemPromptComposer.cs)：固定说明、名称优先的选项呈现 |
| schema 消费者 | [GalateaInputContent.cs](../../prototypes/Galatea/GalateaInputContent.cs) 的 switch、[InternalMail](../../prototypes/Galatea/GalateaDelegationSqliteStore.InternalMail.cs) 的 schema 白名单、共享 CLI projector；新增版本必须同步检查 |
| 大小预算 | `FitsStructuredObservation`、`MaximumConnectionStateJsonUtf8Bytes` 和新 reply lease 的预留；新加两侧名称后重新计算 JSON 最坏转义预算，不能收紧旧 durable 输入的合法性 |

建议顺序：先定新机器快照和纯投影，再做进程内 pending/append 确认，然后接入所有触发与失败出口，最后更新模板及文档并验证。不要先改 prose 就声称完成名称优先呈现。

此阶段无需改辅助提取器的识别语义、主线选路优先级、网页诊断 UI 或 SessionJournal 核心恢复合同。新增领域 input schema 不等于要求迁移既有 Journal。

### 第二阶段验收矩阵

1. 用真实 projector 展示 system、默认回合、普通 override 回合、切换后首回合、后续回合和临时诊断的最终文本，确认 name 主导。不要只测试对象序列化前的字段。
2. 普通新 Observation 的**完整投影**中无 connectionId/modelId 值；system 和首次切换说明包含准确映射。旧历史仍含 ID 是允许的，不进行删除或重写。
3. 诊断覆盖不污染常规选择；同名不同 ID 不被当作没有变化；空名/重复名/特殊字符不会错误选路或突破限额。
4. 首次说明只写入一个新 Observation。append 前失败保留 pending；append 后 generation 失败确认投递，恢复仍带原说明，之后新回合不重复新增。
5. 回合 A 投递旧切换、其 Action 又触发新切换时，对旧事件的确认不能吃掉新事件。明确覆盖这个顺序风险。
6. Player、DelegateReply、心跳、人工与内部来信都携带一致的名称/切换信息；来信不依赖 PlayerTurnObservation 中仅 player-turn enrichment 的支路。
7. 已接受回合的名称、选项、显示选择都冻结；运行时修改或未来配置改名不改变其投影。v1/v2/v3 历史 Observation 和旧 Setup 保留版本化读取/投影。
8. cold restart 与 Undo 的进程状态清理；不新增持久化 outbox 或恢复 override 的扫描过程。
9. 仅角色切换时 system prompt 投影不变；新输入正常追加，不改旧历史。CLI/RecapGrid 对新旧混合输入均可投影。

### 压缩后的现场备忘

- 本节最初用于压缩前交接；后续用户已授权实施第二阶段和生成渲染示例，未要求提交或重启服务。
- 当前基线已有第一阶段，不要重新实现状态提取。当前 git status 另有用户的 `docs/Galatea/feedback/` 未跟踪目录，不属于本次文档编辑，不清理、不覆盖、不顺手提交。
- 本次读取到的配置：cyber 默认 `claude-opus-4-6`，两个 name 为“生活状态／工作状态”，trigger 为裙装／裤装；gpt 默认 `gpt-6-astra-codex`，两个 name 已由用户改为“深入推演／日常生活”，trigger 为戴眼镜／未佩戴。不要将早先示例中的“认真状态／放松状态”写回。具体值会继续变化，施工前只读核对。
- 实际 `.atelia` 配置受 Git ignore；不要把其内容复制为 tracked 实例或碰运行中的角色状态。最新服务是否启动由现场核实，不能沿用旧会话“未重启”的结论。
- 当前机器有 ignored `eng/CompletionDependency.Local.props`，此前验证使用本地开发包；根配置公开 pin 与本机实际依赖可能不同。不得为本功能擅自切换依赖模式或改兄弟仓。
- 第一阶段发现的 nullable scalar 陷阱和两个 artifact 工具继续保留；不要因第二阶段名称显示重新引入工具 JSON null 方案。
- 用户允许繁琐明确子任务委派给 `gpt-6-sol`；不是必须并行。若委派，严格分开 schema/projector、模板、host settlement 和测试文件所有权。重型 .NET 构建测试仍串行，使用 `--no-restore -m:1 -nr:false`。
- 当前测试入口：`CharacterConnectionStateExtractorTests`、`GalateaConnectionStatePromptTests`、`GalateaConnectionStateObservationTests`、`GalateaConnectionStateRuntimeTests`；相关回归有 durable recovery、note receipt、internal mail、recall、CLI structured input。
- 第一阶段测试曾把 provider 的 md-json 文本强行贴成 v1 导致误报；新阶段更新这类假设，但不要把真正的历史输入测试批量改成最新版本。
- DeepSeek live canary 仅合成文本、默认关闭。展示层修改不必再次进行真实 LLM 调用，除非确实修改识别提示词或出现语义风险。

## 第一阶段实施与验证记录（2026-09-25，历史证据）

- 新增辅助 binding、默认禁用的 scaffold、角色 capability gating、SystemInstruction v2 选项快照、Observation v3 连接快照。历史 Setup v1 和 Observation v1/v2 保留原格式读取；没有改写已有 Journal。
- 每角色以一个不可变内存值保存选择及最近切换，接纳时读一次形成回合快照。完成后在角色锁内调用提取器；撤销成功后清空状态。内部信的输入证明和 RecapGrid/CLI 共享投影同步接纳 v3。
- 新 reply lease 的预算预留最大连接快照的 JSON 转义开销；历史输入校验不因此收紧。回归中发现一个 note 测试 helper 未持锁直接追加 Journal，与后台 DerivedInfo 读取竞争，已在该测试 helper 中补锁。
- 初次真实 DeepSeek 样本出现“未提眼镜却选择眼镜状态”的误判，促成显式 unknown 工具。另一个冲突样本表明需先比较叙事和状态摘要，再匹配选项；当前提示词将冲突检查置于选择之前。没有加入模型投票、状态机、历史扫描或业务正则匹配。
- 当前 Completion declaration 对 nullable scalar 只标记可省略，不能据此接收显式 JSON null。因此使用两个互斥 artifact 工具表达结果，不修改上游包或增加兼容层。

验证结果：

| 验证 | 结果及范围 |
|:--|:--|
| 服务端完整非 live 回归 | 1324/1324 通过；最终 unknown 工具调整前运行，覆盖主线、邮件、恢复、note、RecapGrid 既有行为 |
| 最终双工具实现定向测试 | 82/82 通过，覆盖配置、提示词、工具边界、快照和真实宿主路径 |
| 最终提示词调整后提取器与 live 测试 | 11/11 通过，其中 7 个 DeepSeek `deepseek-v4-flash` 真实合成语义样本，另 4 个工具边界测试 |
| CLI 结构化历史验证 | 5/5 通过，包含旧输入与 v3 连接快照混合历史的公开 build 路径 |
| 构建与补丁格式 | 构建零警告、零错误；`git diff --check` 通过 |
| 文档检查 | 本轮文档相对链接无缺失；完整 scoped checker 仍报告 4 条既有 AgentControl 失效链接 |

Live 测试为 opt-in：`ATELIA_RUN_GALATEA_CONNECTION_STATE_LIVE=1`，依赖 `DEEPSEEK_BASE_URL` / `DEEPSEEK_API_KEY`。只发送合成文本，不打开角色会话、不保存 provider 正文。样本通过不等于任意剧情的语义识别都可靠：程序验证结构、角色选项和原文证据，证据是否真的蕴含条件仍依赖模型判断；后续回合的持续状态摘要提供校正机会。

本地 ignored `connections.json` 已加入 `galatea.character-connection-state-extractor: gpt-6-luna-codex`；保留用户填写的四个状态条件。原文件备份位于 `/mnt/wsl/fast/tmp/galatea-connection-state-ujzpakj7/connections.json`。此机器临时路径仅为本次操作记录。未重启 Galatea、未接触角色历史；真实开发会话尚未进行切换验收，DeepSeek 合成 canary 不证明配置的 Codex 辅助连接已在角色会话中运行。

## 第二阶段实施记录（2026-09-25）

- 新写入的连接快照采用 Observation v4：冻结 `effectiveName`、`turnName` 和切换记录的 `previousName`；旧 ID 继续作为机器事实保存。v4 projector 将整个 `connectionState` 转成可见说明，普通回合只有名称，切换时附前后名称、ID、依据。空白名称显示“未命名配置”；同名不同 ID 明确说明连接更新。原始输入不被投影修改。
- 启用该能力的新 Setup 采用 SystemInstruction v3，映射以 name/trigger/connectionId 顺序展示；固定说明只放系统侧。旧 Observation v1/v2/v3 和 Setup v1/v2 保留原投影。输入校验、内部信证明及共享 CLI 投影同步接纳新版本。
- 继续使用不可变 `LastChange` 对象作为进程内 pending 事件，不增加持久化 outbox 或全局计数器。接纳冻结对象引用；fresh `SendAsync` 的 finally 使用最终输入与 exact base head 证明 append。NotAppended 保留，InProgress/Terminal/Terminated 确认，其他证明拒绝。只清除引用相同的事件，且确认发生在完成后提取之前；新 Action 产生的新事件不会被旧确认吃掉。恢复沿用原 Observation，不重新新增通知。
- 连接元数据预算增加到四个名称的最坏 JSON 转义值。若第一封 Ready 回信因完整 note 正文回执占用过大而无法接纳，cutoff 改用保留全部 Memo ID 的紧凑回执预算；最终输入仍由现有 receipt selector 按实际空间选择正文。没有丢弃已保存 Note 的确认身份，也没有收紧历史 durable 输入合法性。
- 测试辅助解码不再把模型可见投影冒充完整持久化 JSON；业务字段校验与原始记录/投影一致性分开。覆盖普通、诊断、切换、重名、空名、多行、旧版精确投影，append 前取消/append 后生成失败，新旧通知隔离、冷启动/Undo 以及 CLI 混合历史。
- 本次没有修改 ignored 实例配置、没有改写角色历史、没有重启服务、没有提交。新模板生效时会形成一次真实 Setup 变化；之后仅状态切换不改变系统内容。

实际渲染示例：`/mnt/wsl/fast/tmp/galatea-name-first-examples-msjott48/examples.md`，同目录保留 `Program.cs` 与独立 reflection harness。调用本轮构建的真实 projector，选项取当前 gpt 角色配置，其余角色上下文、时刻、输入与切换证据是合成数据，不是实际会话日志。包含默认、切换首轮、后续普通、临时诊断、切换与诊断同时出现五种情况。普通及诊断示例完整投影已检查不包含具体连接 ID；system 及切换说明保留映射。

最终验证：服务端完整回归 **1349 通过、1 个 opt-in live 测试跳过、0 失败**；连接状态、输入预处理与 reply lease 定向测试 **95/95**；CLI 新旧混合历史及未知 schema 拒绝测试 **2/2**。构建无警告或错误，`git diff --check` 通过。文档检查仍仅有既存的 4 条 AgentControl 失效链接。本轮未进行真实 provider 调用，测试与示例不代表运行中服务已经加载新版本。
