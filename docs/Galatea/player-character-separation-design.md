# Galatea Player / Character 分离方案

状态：**设计已完成独立审查与本次补充交叉质询；实现进行中，见[实施工作单](player-character-implementation-work-order.md)。**

修订日期：2026-09-16。范围：Galatea 配置、认证、角色运行身份、输入来源、网页交互，以及 SessionJournal 输入/请求恢复、RecapGrid、CharacterMemory、MemoPod 与 sidecar 的必要接入调整。本次修订完善设计约束；实施与验证状态见工作单，真实实例迁移在集成与故障验证后串行进行。

## 1. 最小模型与需求来源

**Character 拥有连续历史和运行状态；Player 拥有外部访问身份；Runtime 持久化结构化内容与可信来源，在请求 LLM 时临时渲染。**

```text
Player 登录 → 选择 Character → 提交带 Player 来源的动作
Runtime 心跳 ───────────────→ Character 的既有 turn runner
Character 来信 / Codex 结果 → Character 的既有 turn runner
```

`players: []` 是正常配置。Character 的存在、状态目录、心跳、收发信和外部委派不依赖 Player。每个 Character 继续拥有一条连续 SessionJournal，不因访问者切换而另开历史。

### 需求账本

| 编号 | 要求 | 来源及地位 |
|:--|:--|:--|
| U1 | 从旧 user 中分离 Characters、Players、Runtime；Player 有自己的 id/name/password。 | 本次用户明确提出并确认。 |
| U2 | 可以没有任何 Player，Character 仍由心跳、来信和外部能力驱动。 | 用户明确要求；沿用已启用角色的后台运行机制，不等于忽略运行门禁。 |
| U3 | 当前所有已认证 Player 都是超级管理员，可访问、交互、停止、撤销所有 Character 的轮次。 | 用户明确选择；以后出现真实受限用户时再增加限制。 |
| U4 | Player 动作保持当前语义；runtime 给广义通信附加可信的来源元数据。 | 用户明确要求。 |
| U5 | 持久化用稳定机读格式，给 LLM 的输入在请求时瞬态渲染，缓存不改变该语义。 | 用户新增并明确确认；优先于前版 FrozenTask 与持久 Markdown 方案。 |
| U6 | 首版可用 md-json，ProjectReference 接入，外置正文路径由局部代码生成。 | 用户提供兄弟项目并认可接入方向；不要求先实现自动路径规则。 |
| B1 | 默认连接、SessionJournal、CharacterMemory、home、邮件和自主活动属于 Character。 | 用户方向 + 当前每个 user 实际只承载一个角色的代码。 |
| B2 | 人工动作、来信、心跳、委派结果保留各自 admission 与处理语义。 | `GalateaFreshInput`、`GalateaAutomaticTurnCoordinator`、现有 HTTP 调用链。 |
| B3 | 一角色一 writer；跨库投递先对账，再允许推进或撤回目标历史。 | `TurnLock`、角色信 outbox / exact Journal proof 的现有运行与崩溃模型。 |
| B4 | 保存业务内容、来源、已选上下文和外部调用事实；旧 frozen 数据按旧合同恢复。 | 当前消费者 + U5；新版不再要求当前 renderer 逐字重建任意旧请求。 |
| D1 | 单文件三个区块、沿用旧 userId 值作为 CharacterId、无通用权限或消息平台。 | 本方案实施选择；身份边界沿用前轮结论，存储/恢复边界按 U5 重审。 |

已确认的权限和推进顺序无需再次提问。ID 的实际取值、旧账号对应哪位真实 Player、实例文件内容属于部署输入，不凭名字自动合并。

### 方案形成时的改造基线及与旧材料的差异

下表记录旧实现为何需要改造，不作为当前施工进度或实现验收；实现证据统一维护在工作单。

| 证据入口 | 基线事实 / 对方案的影响 |
|:--|:--|
| [GalateaConfig](../../prototypes/Galatea/GalateaConfig.cs)、[strict reader](../../prototypes/Galatea/GalateaStrictConfigReader.cs) | V9 `users[]` 混合登录、角色和状态配置；`serverAgentUserIds` 按同一个 ID 关联。 |
| [Program](../../prototypes/Galatea/Program.cs)、[GalateaServices](../../prototypes/Galatea/GalateaServices.cs) | cookie user claim 直接决定 session；认证和交互目标必须拆开。 |
| [HostedService](../../prototypes/Galatea/GalateaServerAgentHostedService.cs)、[角色信 relay](../../prototypes/Galatea/GalateaCharacterMailRelay.cs) | 心跳不依赖浏览器；来信可按需 attach 目标，不要求目标进入心跳名单。 |
| [FreshInput](../../prototypes/Galatea/GalateaFreshInput.cs)、[PlayerTurnObservation](../../prototypes/Galatea/PlayerTurnObservation.cs) | 当前先 Wrap，再把文本写入 Journal；分型只停留在内存，须贯穿持久化与查询。 |
| [prompt composer](../../prototypes/Galatea/GalateaSystemPromptComposer.cs)、[RecapGrid assets](../../prototypes/Galatea.RecapGrid/GalateaRecapGridAssets.cs) | 两者都依赖固定 PlayerName；只拆配置不能使零 Player 的新建角色完整运行。 |
| [dispatch contract](../../prototypes/Galatea/GalateaDelegationDurableContract.cs)、[snapshot](../../prototypes/Galatea/GalateaDelegationSqliteStore.Snapshot.cs)、[CharacterMemory store](../../prototypes/Galatea/CharacterMemory/CharacterMemorySqliteStore.cs) | 旧 userId 进入 store owner 与 dispatch 计算。值不变可保留身份；不能重新生成随机 ID 后只改 meta。 |
| [delegation driver](../../prototypes/Galatea/GalateaDurableDelegationDriver.cs)、[operator recovery](../../prototypes/Galatea/GalateaDelegationOperatorRecovery.cs) | 当前 Start、Inspect 和恢复任务核对都读取 mail.Body；新版共同切到临时任务与持久发送证据。 |
| [target alignment](../../prototypes/Galatea/GalateaRecapGridTargetAlignment.cs)、[CLI asset catalog](../../prototypes/SessionJournal.Cli/RecapGridOperatorAssetCatalog.cs) | fresh admission 精确核对 active target；CLI 也要求 PlayerName，必须随新资产一起调整。 |
| [角色信设计](character-mail-design.md) | 当前角色名必须唯一，且精确 `Codex` 保留；capture 冻结目标 userId / repository locator。 |

早期 [身份演进研究](user-identity-evolution-research.md) 以 account/login 解耦为前提，建议额外 accountId、loginName 和历史 dispatch key。它提供影响面线索，但不构成本方案需求依据；其“可重复 characterName”假设不符合站内信的角色地址规则，其 account 目标也已被本次用户选择替代。保留该原稿作为历史材料。

## 2. 配置与身份归属

继续使用一个 `config.json`，V10 分为 `characters`、`players`、`runtime` 三个区块；`v` 仍是根格式版本，由旧 V9 转换。示意字段如下，省略现有必需路径和 Runtime 细节，不是可直接运行的配置：

```json
{
  "v": 10,
  "characters": [
    {
      "id": "character-a",
      "name": "角色甲",
      "defaultConnectionId": "main-model",
      "heartbeatEnabled": true
    }
  ],
  "players": [
    { "id": "player-main", "name": "玩家", "password": "<配置凭据>" }
  ],
  "runtime": {
    "listenUrls": ["http://127.0.0.1:3510"],
    "maintenanceMode": false
  }
}
```

### Characters

- `id` 是稳定 `CharacterId`；`name` 是角色名。原 `sessionDir`、`delegationStateDir`、`characterMemoryStateDir`、`homeDir`、`sessionProvisioning`、默认连接和角色 context 配置迁入这里。
- `heartbeatEnabled` 是每角色唯一周期 pulse enrollment 配置；省略为 false。移除根 `serverAgentUserIds`，后台列表只从此字段派生。沿用原循环先检查 Ready 回信、再判断空闲激活的政策；它不控制独立的角色信 relay，也不是角色全部活动的总开关。
- runtime 的角色字典、session cache、extractor、recall / RecapGrid binding、mailbox、home 与 delegation 均按 CharacterId 取得。`UserSessionHost` 改为角色会话命名。
- ID 在角色集合内 ordinal 唯一且不随改名改变；沿用能接纳现有 ID 的验证规则，不要求为了本次重构换成 UUID。目录隔离和 owner 校验保留。
- 当前角色名仍在角色地址簿内唯一，`Codex` 仍保留。ID 稳定不代表本期支持在线任意改名；未决邮件涉及名字匹配的现有限制见第 6 节。

### Players

- 只持有 `id`、`name`、`password`，不持有 characterId、会话或任何角色状态目录。
- 本期以 PlayerId + password 登录，name 用于显示和输入来源。无需另加 loginName、accountId、role 或权限表。
- PlayerId 在 Player 集合内唯一；与 CharacterId 可以同值，但调用边界必须区分种类。删除或修改 Player 不修改 Character 状态。
- `players` 必须是数组，允许 `[]`；所有已认证且仍在配置中的 Player 具有相同管理权限。旧 cookie 失效并重新登录，不能把旧 user claim 当 PlayerId 自动接受。

### Runtime

- 根部现有 `listenUrls`、`callLogDir`、`maintenanceMode`、`recapGrid` 归到这里。全局维护模式继续约束所有角色的写入。
- `connections.json`、`delegates.json` 保持独立文件及既有路径解析；Runtime 使用它们提供的服务，不复制连接清单。
- 所有相对路径继续相对于原有配置基准解析；仅 JSON 层次变化不改变目录身份。

## 3. 认证、网页与 API

### 两个独立的请求维度

```text
已认证 PlayerId → 身份有效，允许当前管理操作
显式 CharacterId → 本次操作的目标会话
```

建议保留 `/api/v1/me` 返回当前 Player 身份与全局状态；新增只读角色列表。角色操作统一放到 `/api/v1/characters/{characterId}/...` 下，现有 operation 后缀、请求与 SSE 语义尽量保留。迁移所有近期历史、状态、recap/timeline、chat、stop、undo、recovery、retry、mailbox 路由，不能只处理发送入口。

API 边界集中解析当前 Player 和目标 Character，不引入权限引擎。目标不存在返回明确错误，不退回某个默认角色。后台调用直接按 CharacterId 进入 service，不制造虚拟 Player 或借用管理员 cookie。

网页登录后提供角色选择。为减小切换竞争，初版选择角色后整页导航到含 CharacterId 的页面；该页面的请求目标固定。既有持久模型偏好按 `(PlayerId, CharacterId)` 隔离；输入框草稿仍是当前页面的临时状态，本期不新增跨页草稿保存。异步响应不能进入另一角色页面。未选择角色时展示选择入口，不在服务器保存一个全局“当前角色”。

超级管理员可以操作所有角色，但仍须满足 TurnLock、Busy、恢复和邮件 proof 门禁。撤销角色轮次不撤销已经发出的外部工作或已经 Delivered 的邮件，沿用现有语义。

## 4. 稳定内容与瞬态渲染

完整合同、消费者地图与故障验收集中在 [结构化输入存储与瞬态渲染](structured-input-rendering-design.md)，本节只定义身份方案与它的衔接。

- Player 动作、角色邮件、Codex 回信、心跳、Note receipt、recall 等按类型保存结构化事实和原始正文；JSON schema 版本描述字段语义。
- 发送者来源由 runtime 确认并在接纳/capture 时保存名称快照。组合 Observation 各块分别归因；HTTP 注入者与客户端声明的信内署名分别记录。
- SessionJournal 追加机读输入；recent、undo、投递 proof、DerivedInfo 等直接读字段。给主线和辅助 LLM 的 prompt 在各自请求组装边界投影。
- 网页可从机读字段独立生成显示文本；纯查询与审计不依赖 LLM 输入投影器。显示文本不成为输入存储、投递证明或身份比较的权威。
- 辅助请求保留其选中块的来源、时间和类型含义；业务解释属于稳定 schema/指令源。MemoPod 的 Open/Freeze 保存并冻结机读内容，到 Recall 请求时才渲染；清缓存不改变内容身份或冻结周期。
- md-json 是局部 renderer，ProjectReference 接入；JSON Pointer 列表由当前内容 shape 生成，不进入角色配置或持久输入。
- 新写入不保存 FrozenTask、渲染后的 Observation/receipt、渲染上下文快照或持久渲染缓存。原正文中的 Markdown、业务指令源文本、LLM 输出和必要 provider opaque 协议内容仍是内容事实。
- Prepared 只保存所选内容计划，每次 Started/dispatch claim 记录该次请求承诺；未发送时换风格不增加 Prepared 或结束事件。已发送未知的尝试保留原证据并先核对，不因换风格获得重发权限。
- 旧版本已有的 Prepared、Bound、Applied receipt 等保留精确读取与原恢复路径。身份拆分不授权将历史作者或旧外部请求改写成当前配置。
- 旧 Pending 回执可作为明确标记的 legacy 内容进入新结构化输入；旧内容来源格式与本次绑定格式分别判别。已 Bound 的原输入不重新包装或改写。
- 角色输出的网页/响应上下文明确 CharacterId 与轮次，并保留角色、旁白、状态摘要各声部。一个角色会话的所有输出不等于该角色的一次发言。

这项变化与身份拆分一起做纵向接入；先只改 DTO、仍把包装好的文本落盘，不能算本方案完成。

## 5. 去掉 prompt 与 RecapGrid 的固定 Player 绑定

Character 系统 prompt 只依赖角色身份、角色设定、home、能力及 roster；不依赖某个登录 Player。当前动作的 Player 来源进入 Observation。角色与某位人的既有关系保留为角色设定中的叙事事实。

- 新 character context 不要求 `${playerName}`，固定玩家来源的假设改为从输入来源识别参与者。新版 SystemPromptSetup 保存有序指令源与当时 Character/home/能力/roster 绑定事实，DesiredSetup 比较语义内容；业务模板语言保持局部，实际拼接/包装只在请求时生成。
- 旧自定义模板若使用 `${playerName}`，离线转换可用旧配置的 PlayerName 将该 token 一次替换为原文字面值，保留关系设定；不能在运行时选择第一个 Player 兜底。转换必须保留既有非递归模板语义并校验结果。
- 新建 Session 的 RecapGrid asset 参数与 world/autobiography prompt 去掉必需 PlayerName，仍以 Character 视角组织摘要；采用新的 asset 标识。CLI `scaffold` / `control provision-asset` 的 catalog 同步接入这一个新定义，解除其必需 `--player-name`，不另造 Galatea 专用资产命令。

### 既有 RecapGrid 的采用边界

旧 PlayerName 已展开为持久字符串，恢复这些资产不需要 Player 账号。**但去掉模板中的固定 Player 会改变两个 Maintainer definition digest，旧 active target 不等于新目标。** 保留 `RequireCurrent`：现有测试能证实它会阻止错误角色、混合定义、错误列序与列数，不是单纯名称兼容门禁。

[CreateOverlay](../../prototypes/SessionJournal.RecapGrid/Abstractions/BuildContracts.cs) 要求变更定义的列重新计算，[row build](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerRowBuild.cs) 只复用相同 definition 的 cell，[progression](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerProgression.cs) 按行建立新 recipe。因此本方案不承诺零重算；新定义采用会按现有机制重建受影响的派生摘要列，需据实例行数估算 provider 调用与耗时。

最小顺序是：

1. 保留旧 recipe、Family / Maintainer、cell、active build 及 raw Journal，不原地改写。注册新资产本身不抢换旧 active，也不构成已经采用。
2. 旧 schema 的 `FrozenCompletionRequired` 按既有 BindPrepared 路径恢复原请求字节；新版按专题中的内容计划/尝试合同处理。身份/资产/风格变更均不自动授权重试 uncertain Completion。
3. `ToolContinuationRequired` 先用 frozen profile 结算 pending tool 到 ToolResult 边界。随后进入新 completion 时仍检查当前 target；不匹配就明确停在该边界，不能重跑已结算工具或略过门禁。
4. 使用已有 operator 路径完成新 recipe 的构建、Fulfillment 验证与 promotion；在需要新定义的新 completion 前完成采用。旧 build 若尚未到可切换边界，按既有维护/恢复流程处理，不另造双 active 或 MigrationMode。
5. 采用后，fresh 和 continuation 的新请求都通过新 target 校验。历史 frozen 请求仍使用其原依赖，旧摘要产物和 raw 事实不被新构建覆写。只改 md-json 布局/围栏不改变摘要语义定义，不应触发这套资产重建。

以上是一次明确的派生状态更新，与角色 owner 值的保持是两件事。不增加 legacyPlayerName、备用 expected target 或“从下一行换摘要算法”的新机制来回避已有构建流程。

## 6. 迁移：保留角色身份值与历史事实

### 优先采用的路径

对每个旧 user，**新 CharacterId 等于旧 userId，所有角色相关路径与 Character name 保持不变。** 新 Player 单独配置，账号如何合并由操作者明确给出。代码中的 owner 概念改为 Character；不顺带轮换 durable key。

因此，不引入 accountId、legacy dispatch key、旧新 ID 映射表或 dispatch 双算法。旧 `gd1-*` dispatch 输入字节、owner 值、repository identity、home 路由及外部工作关联可以保持不变。

本期不为改拼写单独升级 SQLite：既有 `user_id` / `target_user_id` 或恢复 evidence 的历史字段可由局部持久化读写代码映射为 CharacterId，格式保持一套严格读写。业务代码、配置、HTTP 不继续保留混合 User 概念。这是保留既有持久格式，不是支持两套业务身份模型。

本次不新增 FrozenTask。结构化输入涉及 SessionJournal 内容/Prepared、delegation outbox/reply lease、CharacterMemory receipt 的真实 schema 与恢复边界；按专题逐项迁移。保留 owner 值只能免去身份轮换，不能承诺这些库完全不变。

### 操作与验证边界

1. 配置/模板采用一次性离线转换步骤或脚本，不预设长期 CLI 或可扩展迁移框架；先用合成隔离实例验证。新 runtime 仅读新配置，不保留旧 users 配置兜底。
2. 实例转换前停服、取得既有独占锁并备份配置、模板及相关状态；只读盘点两类 owner、未决邮件和当前 RecapGrid 依赖。转换先预览/dry-run，显式 apply 才写入；不从旧账号同名自动推断应合并哪些 Player。
3. 分别验收配置/模板转换、结构化输入与调用事实的 schema 接入、RecapGrid 新语义定义采用。owner 值、路径、角色名、dispatch / reply / outbox locator 保持一致；旧 raw 历史不原地改写。先按旧格式验证，按真实域升级后 strict reopen；旧未决工作保留其证据与原核对语义。
4. 不以“队列必须清空”强迫重试未知外部工作；保留原身份的未决工作按已有规则恢复。若某个实际格式升级无法保持该状态，应报告具体阻塞及独立处理步骤。
5. 不改 live 状态来获得 green evidence，不删除 store、重发 OutcomeUnknown，也不读取或改写 Codex 私有 SQLite。
6. 跨文件、数据库和 RecapGrid control 更新不声称有全局事务。按步骤保留备份与结果，启动使用匹配的新配置/schema；target 由既有新请求门禁检查，不因尚未采用新 target 就封死只读、维护或 frozen recovery 入口。若仅转换配置可回退该配置；若已升级数据库或采用新 recipe，则按对应步骤恢复匹配备份/既有控制状态，不能声称回退一个 JSON 就足够。恢复正常运行后产生的新状态不得被旧备份覆盖。

稳定 ID 不自动解除当前站内信对角色名和目录的约束。修改 Character name、搬目录、删角色或同路径换仓仍须按旧配置盘点 Pending / Bound / Quarantined；不能把未决信按新 roster 重新路由。本期沿用旧值避开这些变化。

## 7. 最小施工顺序与验收

### 切片 A：零 Player 与一个管理员的结构化主线

在合成配置中一起完成：新 strict config → Character host / 无固定 Player 的 prompt、RecapGrid 资产及 CLI → 零 Player 心跳 → 管理员登录并选择角色 → 结构化动作落盘 → md-json 瞬态请求 → recent / undo 直接读取 → 新版请求尝试及恢复边界。内部可小步提交，不以不能运行的纯类型骨架作为验收。

### 切片 B：全部通信、派生输入与旧状态

接通角色间邮件、Codex 出站与 sidecar 核对、reply lease、Note receipt、recall、recap、MemoPod 冻结/缓存边界、辅助 LLM 请求、配置转换与旧数据读取。删除所有新版先渲染后落盘旁路。新旧混合状态、crash/reopen 与格式替换验收以 [专题矩阵](structured-input-rendering-design.md) 为准。

| 身份侧验收 | 通过标准 |
|:--|:--|
| 零 Player 两个 Character，无浏览器 | 周期驱动、A→B 邮件和 fake Codex 结果续接均不需要虚拟 Player。 |
| 管理员访问 A/B | 所有状态、动作、stop/undo/recovery 只作用于显式目标；不存在时不回退。 |
| 移除或改名 Player | Character 状态不变，旧作者快照不变；移除后的身份不能继续使用受保护 API。 |
| Player body 或 HTTP from 自称 Character/Codex | 可信来源保持真实接纳者，声明署名不升级为认证身份。 |
| 动作附带回信、Note、recall | 分块来源保留，undo 只还原动作；改变 renderer 不改持久内容。 |
| 全局 maintenance/stopping、角色 busy/failed | 管理权限不绕过既有写门禁与 uncertain 处理。 |
| 旧配置与真实状态迁移 | ID/路径不变，旧未决证明仍成立；新 renderer 不参与旧冻结字节核对。 |
| 新语义 recap 资产/ToolContinuation | 旧工具只结算一次，新 target 采用前不开始不匹配的新 completion；纯排版变化无需重建。 |

主要入口：[config tests](../../tests/Galatea.Server.Tests/GalateaConfigValidationTests.cs)、[strict fields](../../tests/Galatea.Server.Tests/GalateaRootConfigFieldLanguageTests.cs)、[Observation tests](../../tests/Galatea.Server.Tests/PlayerTurnObservationTests.cs)、[provisioning](../../tests/Galatea.Server.Tests/GalateaSessionProvisioningTests.cs)、[角色信 delivery](../../tests/Galatea.Server.Tests/GalateaCharacterMailDeliveryTests.cs)、[operator recovery](../../tests/Galatea.Server.Tests/GalateaDelegationOperatorRecoveryTests.cs)、[RecapGrid target / recovery](../../tests/Galatea.Server.Tests/GalateaRecapGridCompositionTests.cs)、[public operator chain](../../tests/Galatea.Server.Tests/GalateaRecapGridPublicOperatorChainTests.cs)。跨 SessionJournal 和 sidecar 的新增验收不能仅由原 Galatea 单测替代。

实现时同步配置/API/runtime 参考、运行指南及相关 current contracts；文档索引按实际施工与验证状态标记，不把设计完成等同于实施完成。受限权限、持久草稿、通用路径选择框架仍不在范围内。

## 8. 审查结论与已替代方案

前轮身份审查保留的结论：两个业务身份、一文件三部分、一份 heartbeat enrollment、按目标寻址、单级管理员，不增加 accountId、权限引擎或 ID 映射。

当前用户 U5 替代了前轮 FrozenTask 结论；“只多一个持久 task 字段”和“CharacterMemory 无需升级”已不再是当前方案。新的不可约核心是结构化内容、瞬态投影与实际调用事实之间的边界。md-json 已有真实库，本期接入，不再列为整体暂缓。

前轮三位 subagent 完成独立查漏与交叉质询，主线程据代码证据修订；补齐了 SystemPromptSetup/audit、receipt 内容选择、recall 的 title/exactText、Recap 语义协议与 CLI 日志，删去了 supersede 事件、新提交 ID 和统一辅助调用 WAL。本次再由三位审阅者复核并交叉质询，补齐 MemoPod 早渲染、辅助输入来源、旧 Pending 与新绑定混用、机读支持域和失败阶段；裁决汇总见专题第 8 节。

实施前仍须盘点实例 schema、容量和未决状态，确定实际 Player 配置及依赖源码版本。本轮完成的是设计与验证要求，不是代码实现、状态迁移或真实 provider 验收。
