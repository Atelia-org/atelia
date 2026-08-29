# Galatea per-user 角色名模板化设计

状态：**Implemented；current product contract 以代码、版本化合同与本文“实施结果”为准**
日期：2026-08-28
范围：`prototypes/Galatea` 主会话 system prompt、mail/extractor generated prompts、
`prototypes/Galatea.RecapGrid` code-owned rolling recap asset，以及它们与 per-user
`config.json` 的一致性。

> **2026-08-29 V5 follow-up：** current root contract已hard-cut到
> [V5](../SessionJournal/current/contracts/galatea-root-config-v5.md)。V4的完整
> `systemPromptTemplate*`被`characterContextTemplate*`取代，主prompt改由code-owned protocol prefix、
> operator context与universal code-owned mailbox base固定合成；validated outbound binding非`null`时再追加
> code-owned Codex outbound appendix。见
> [system prompt protocol/context设计](system-prompt-protocol-context-design.md)。本文关于character/player
> name、RecapGrid V6、mail/extractor、envelope与alignment gate的实施结论仍有效；下文V4/full-template叙述保留
> 其历史时态，不是current field language。

## 0. 实施结果与设计收口

本设计已实施。下文保留实施前的问题分析和决策理由；若与本节或current
contracts冲突，以本节和current contracts为准。最终产品形状是：

- root config current已hard-cut到V5，每个user必须显式配置`characterName`与`playerName`；file DTO只保存
  `characterContextTemplate*`，runtime DTO只保存finalized `SystemPrompt`。Host load固定组合code-owned prefix、
  operator context与universal mailbox base；validated outbound binding非`null`时追加code-owned Codex appendix。
- 新建窄小的`Galatea.Prompts` assembly，只提供两个name value objects与closed、
  non-recursive、bounded的`${characterName}` / `${playerName}` renderer；不引入通用模板引擎。
- RecapGrid hard-cut到`galatea-rolling-rewrite-zh-cn-v6`；`scaffold`与`provision-asset`必须提供
  `--character-name`与`--player-name`。`Galatea` + `刘世超`参数保持V5 canonical bundle四个digest exact不变。
- 每个user在composition阶段获得immutable outbound extractor，共享底层lazy Completion client；
  extractor V2 `ContractId`指纹包含渲染后语义合同，但不包含provider/model/connection路由。
- inbound mail保持原XML schema，消息自身冻结validated `To`。Player-turn Observation保持原prefix和
  delegation store schema V1；新writer使用角色中立heading，reader exact支持新旧两种闭集dialect。
- fresh/current路径只比较active recipe的typed `BuildTargetDigest`与当前user的V6 expectation；
  mismatch fail closed为`character-asset-mismatch`并隐藏Context header。Frozen recovery完全使用已冻结身份。
- 首版只支持“不同user从各自session起点使用不同character/player names”，不支持existing-session hot rename；
  不增加alias/transition coordinator、`asset describe`、receipt identity新版、mail/player envelope V2或SQLite schema列。

这些删减来自实施期间的独立复杂度审计：它们不是当前“每个user可以从起点配置角色名”
所必需的边界。若未来真正需要已有session改名，再以独立设计处理historical marker aliases与derived
state migration，不在本轮预留半套兼容机制。

2026-08-28 follow-up在同一未运行的V4 delta中补齐`playerName`与missing-template bootstrap；
细节见[playerName 与内建template设计](player-name-and-default-template-design.md)。下文的“单变量”、
“只有character name参数”表述是该follow-up之前的实施历史，不是current contract。

## 1. 结论

推荐把 `config.json` 中每个 user 的 required `characterName` 设为角色名的唯一
operator authority，但不要在 provider 请求阶段做全局 `string.Replace("Galatea", ...)`。
同一份已校验角色名应在各自明确的生命周期中分别物化：

```text
config V4 user.characterName
        |
        +-- host load -- render system prompt source template
        |                    |
        |                    +-- finalized SystemPrompt
        |                          -> SessionJournal SystemPromptSetup
        |
        +-- operator -- render Galatea RecapGrid V6 member templates
        |                    |
        |                    +-- canonical Definition digests
        |                          -> per-session Recipe / Cells / Context header
        |
        +-- host load -- render per-user outbound-mail extractor contract
                             |
                             +-- extractor ContractId + role evidence rules

fresh/current turn: current config与active V6 definitions必须exact相符
frozen recovery:    只恢复已冻结的旧prompt/definition/request，不重新模板化
```

这些物化共用一个很小的、强类型的角色名/模板合同，但输出分别由
SessionJournal、RecapGrid 和 delegation capture 的既有 durable authority 接管。这样既能支持同一 host 中不同
user 使用不同角色名，也不会破坏 Prepared request 重建、Definition digest、Cell reuse 和
replay determinism。

建议实施时 hard-cut root config 到 V4：missing `characterName` 直接拒绝，绝不回退到
`"Galatea"`。角色改名应被视为一次显式语义迁移，而不是热更新显示字段。

## 2. 实施前已验证的现状与边界（历史快照）

### 2.1 主会话 prompt 链

当前 [`GalateaUserConfig`](../../prototypes/Galatea/GalateaConfig.cs) 同时充当 file DTO 与
resolved runtime config，包含 `SystemPrompt` / `SystemPromptFile`，但没有角色身份字段。
[`GalateaConfigLoader`](../../prototypes/Galatea/GalateaServices.cs) 在启动时读取
`systemPromptFile`，以文件内容覆盖 inline prompt；新 repository bootstrap 使用最终文本创建
`SystemPromptSetup`。每个 fresh turn 又在 exact Idle head 通过
`ReconcileDesiredSetup` 把当前 resolved prompt 写成新的 governing setup；已有 Prepared/frozen
request 则从 durable setup 重建，不读取 prompt 文件来改写历史请求。

因此模板必须在 config load/materialization 阶段完成，`GalateaUserConfig.SystemPrompt` 在进入
session runtime 后仍应只表示**已经完成模板展开的最终文本**。

### 2.2 RecapGrid prompt 链

[`Galatea.RecapGrid.csproj`](../../prototypes/Galatea.RecapGrid/Galatea.RecapGrid.csproj) 只把以下三份
tracked prompt 作为 embedded runtime resource：

| Resource | 当前角色名状态 | Runtime 作用 |
|:--|:--|:--|
| [`recap-maintainer-family/system-zh-cn.md`](prompt/recap-maintainer-family/system-zh-cn.md) | 已经是角色无关文本 | shared `FamilyDefinition.SystemPrompt` |
| [`world-understanding/rewrite-zh-cn/user.md`](prompt/world-understanding-maintainer/rewrite-zh-cn/user.md) | 多处写死 `Galatea` / `[Galatea]` | world Definition 的 member prompt |
| [`autobiographical/rewrite-zh-cn/user.md`](prompt/autobiographical-maintainer/rewrite-zh-cn/user.md) | 多处写死 `Galatea` / `[Galatea]` | autobiography Definition 的 member prompt |

[`GalateaRecapGridAssets`](../../prototypes/Galatea.RecapGrid/GalateaRecapGridAssets.cs) 在 operator
scaffold/provision 时读取资源，并把 member prompt、topic、`ContextHeaderBlockTarget` heading
一起写进 canonical Definitions。Runtime 的 `MaintainerDeclarativeSpec.UserPromptTemplate` 虽然
名字含 `Template`，却已经是 Definition 内的 finalized durable 字符串；
[`RuntimeRenderer`](../../prototypes/SessionJournal.RecapGrid/Runtime/RuntimeRenderer.cs) 只把它序列化到
maintainer work tail，不负责替换任意 Galatea 变量。

所以 RecapGrid source template 必须在**构造 registration bundle 之前**展开。不能在 provider
dispatch、Context header materialization 或 Cell 读取时替换，否则 provider 实际输入会与
Definition/EvaluationKey 宣称的语义身份分叉。

### 2.3 邮箱还有第三条角色 prompt 链

[`OutboundMailExtractor`](../../prototypes/Galatea/GalateaMailbox.cs) 的 code-owned system/user prompt
和 tool field descriptions 也写死了 `Galatea` / `[Galatea]`。当前 production composition 只构造一个
host-wide extractor，再把它交给所有 per-user `GalateaOutboundExtractionReconciler`；角色名可配置后，
这会把不同 user 的 Action 都按同一个角色 marker 解释。

这条链还有 durable 约束：`IOutboundMailExtractor.ContractId` 被写入
`action_capture.extractor_contract_id`，已完成 capture 不能在重启后换一套未标识的新 prompt 语义。
此外，inbound mail XML 的 `to="Galatea"`，以及 Codex reply/delivery-failure observation headings 也会
作为 LLM 可见的 raw Observation 持久化。它们虽不是 operator-authored Markdown prompt，仍属于本次角色
身份审计；custom character 不应继续在故事里收到“给 Galatea 的回信”。

正确收口见 §7。只修改用户点名的三个 Markdown 文件仍会留下 silent split。

### 2.4 当前 prompt 文件不处于同一 authority 层

- `.atelia/galatea/prompts/*.md` 与实际 `config.json` 是 ignored machine-local runtime state；
  它们可以由多个 user 分别引用，也可以共享同一 template 文件，但不属于 tracked product
  authority。当前 inspected config exact 引用 `cyber.md` 与 `gpt.md`；同目录 `cyber_template.md` 存在但
  未被 config 引用，不能算 active runtime input。
- 上表三份 zh-CN 文件是当前 RecapGrid binary 的 code-owned runtime resources。
- 当时的单文件tracked host template是prompt文档，但当时没有
  runtime loader 引用；它适合作为 source template 示例，不能被描述成当前 host 的自动真源。
  该路径后来由V5 fixed protocol/context composition取代；current导航见[prompt router](prompt/README.md)。
- 同目录旧的 English recording/rewrite/compression prompt 目前没有被
  `Galatea.RecapGrid.csproj` embed。实施时应明确标为 historical/superseded，或在确认仍有用途后
  一并迁为模板；不能因文件位于 `prompt/` 下就声称它们已经进入 current request。

## 3. 身份分类：哪些 `Galatea` 应替换

`Galatea` 同时被用作产品名与角色名。模板化前必须先分开这两种身份：

| 类别 | 处理 |
|:--|:--|
| 项目、assembly、namespace、C# type、HTTP product、CSS/JS 名称 | 保留 `Galatea`；这是 product identity |
| connection binding key，例如 `galatea.input-normalizer` | 保留；这是稳定的 product protocol identity |
| operator asset ID 前缀，例如建议的 `galatea-rolling-rewrite-zh-cn-v6` | 保留；asset 属于 Galatea 产品，不属于某个角色 |
| 自然语言中的角色称呼、`[Galatea]` voice marker、member topic、semantic heading | 用 `${characterName}` 展开 |
| `galatea.world-understanding` / `galatea.first-person-autobiography` block key | 保留；这是 Galatea product 的稳定 machine key，不是显示名 |
| mailbox/extractor 的 natural-language `Galatea`、`[Galatea]` | 模板化或改成明确的角色中立 protocol wording |
| `刘世超` 等玩家/外界人物名字 | 不由 `characterName` 改写；这是另一项故事身份，不能偷用 login `userId` |

`galatea.*` block keys 和 `atelia.galatea.*` contract IDs 类似：小写前缀表达产品 owner，具体
semantic heading 才表达该 user 的角色显示名。不要仅因为某些角色会生成新 Definitions，就顺手扩大
provider-header/API machine identity 的变化面。Logical column IDs `world-understanding` 与
`autobiography` 已经中立，也应保持不变。

“角色名可配置”不自动解决称谓、代词、别名、玩家名或 persona 配置。本轮应只替换角色专名与 marker，
不要顺手改写代词或人物设定。Recap V6 在 `characterName:"Galatea"` 下应保持 V5 canonical bytes；主
system prompt 则要把当前抽象 `[角色名]` 输出说明收紧成 exact `[${characterName}]`，所以其 finalized
文本会有一次有意变化。若以后要支持不同代词或多个主要 NPC，应另立 character profile 设计。

当前主 prompt 中的 `加拉泰亚` 是 `Galatea` 的中文别名。可复用的 generic template 不能把这个别名留给
其他 `characterName`；应删除该 parenthetical alias，或把它明确留在只服务 cyber/Galatea 的 machine-local
template 段。`characterName` 本身只承担 primary exact marker，不自动翻译或生成别名。

## 4. 推荐 root config V4 合同

建议把现有 V3 的 prompt 字段同时改名，避免字段表面继续声称自己保存的是 finalized prompt：

```json
{
  "v": 4,
  "users": [
    {
      "userId": "cyber",
      "password": "REPLACE_WITH_A_PRIVATE_PASSWORD",
      "characterName": "Galatea",
      "playerName": "刘世超",
      "sessionDir": "sessions/cyber",
      "delegationStateDir": "delegation-state/cyber",
      "sessionProvisioning": "existing-only",
      "systemPromptTemplate": "",
      "systemPromptTemplateFile": "prompts/cyber.md"
    },
    {
      "userId": "gpt",
      "password": "REPLACE_WITH_A_PRIVATE_PASSWORD",
      "characterName": "Alice",
      "playerName": "Alex",
      "sessionDir": "sessions/gpt",
      "delegationStateDir": "delegation-state/gpt",
      "sessionProvisioning": "create-if-missing",
      "systemPromptTemplateFile": "prompts/gpt.md"
    }
  ],
  "recapGrid": {
    "routeManifestPath": "recap-grid-routes.json",
    "agentControlProfileFiles": ["recap-grid-agent-control-profile.json"],
    "currentAgentControlProfileId": "default"
  }
}
```

推荐 exact 语义如下：

- `characterName` 是每个 user 的 required string；没有 root/global default，不从 `userId`、文件名、
  SessionJournal 内容或旧 `Galatea` 文本推断。
- `systemPromptTemplate` 与 `systemPromptTemplateFile` 沿用 V3 的 inline/file precedence：有效 file
  覆盖 inline；二者最终必须提供 nonblank source template。
- V4 reader 只接受新字段；删除 `systemPrompt` / `systemPromptFile`，不保留双字段或自动解释。
- file DTO 与 resolved runtime config 应拆开。建议内部 `GalateaUserFileConfig` 保存 template 字段，
  public/runtime `GalateaUserConfig` 保存 validated `CharacterName` 与 finalized `SystemPrompt`。这样后续
  session、extractor 和测试 helper 不会误把未展开模板当作 provider prompt。
- bootstrap writer 为每个示例 user 显式写 `characterName` 和 template 字段；已有文件仍不由应用
  自动重写。

V3 合同应在实现 V4 时变为 archived predecessor，并新增
`docs/SessionJournal/current/contracts/galatea-root-config-v4.md`；在代码真正 hard-cut 之前，本设计文档
不修改 V3 的 current 标记。

## 5. 小型、封闭的模板语言

不要引入 Liquid、Handlebars、Scriban、反射字典或通用表达式求值。当前只需要一个 code-owned token：

```text
${characterName}
```

选择 `${...}` 而不是 `{{...}}`，是为了不与当时单文件host template中用于memory slots的`{{}}`
占位文本混成同一种语言。

示例 source template：

```markdown
唯一主要NPC **${characterName}** 生活在一个特别的赛博空间。

每次回复先输出 **[${characterName}]** 第一人称内容，再输出 **[旁白]**。
```

建议由新的小型 product contract assembly（例如
`prototypes/Galatea.Prompts`, namespace `Atelia.Galatea.Prompts`）唯一拥有：

- validated `GalateaCharacterName` value object；
- exact token 常量；
- 单次、non-recursive、bounded template renderer；
- source token scan 与 rendered UTF-8 byte bound。

`Galatea.Server` 与 `Galatea.RecapGrid` 都引用这一小层；不要复制两套 validation，也不要让主会话
prompt loader 反向依赖 RecapGrid。该 assembly 不引用 provider、SessionJournal、RecapGrid runtime 或
Galatea.Server。

### 5.1 `characterName` validation

角色名会进入 `[角色名]` marker、Markdown 和 canonical Definitions，应该把它当作 label，而不是任意
prompt fragment。建议 exact 规则为：

- strict UTF-16/Rune sequence，NFC；输入若不是 NFC 就拒绝，不静默 normalize；
- UTF-8 长度 1..128 bytes，且前后没有 whitespace；
- 不做 ASCII-only 或自然语言内容白名单；中文、重音字符、符号/emoji 都可以是 operator 选择的名字；
- 拒绝 control/line/paragraph separator；format rune只允许emoji grapheme需要的U+200D ZWJ，
  且名字必须至少包含一个non-format rune；
- 拒绝会破坏 voice/template grammar 的`[`、`]`、`$`、`{`、`}`；
- exact 拒绝保留字 `旁白`、`状态摘要`、`角色名`，避免与现有 output marker 冲突；
- 不要求不同 user 的角色名 unique，因为每个 user/session 是隔离 authority。

别名、括号内译名和长人物描述不应塞进 `characterName`；它们留在 source template 的人物设定中。

### 5.2 Renderer 的 fail-closed 规则

- character-dependent source template 至少出现一次 exact `${characterName}`；
- 遇到任何其他 `${...}`、残缺 `${` 或渲染后仍有 token opener，拒绝；
- 只扫描一次，replacement 中的字符永不再次解释；
- 先计算 exact rendered UTF-8 byte length，再分配输出；source 与 rendered output 都必须分别满足
  destination owner 的既有 cap；
- 保持除 token 外的所有字符、换行与空白 exact 不变。Main system prompt 是否 `Trim()` 应由 V4
  明文延续或修订，不能由 renderer 偷做；embedded Recap prompt 继续保持 strict UTF-8、no BOM、LF-only。

`recap-maintainer-family/system-zh-cn.md` 当前不含角色身份，应该继续作为普通静态 resource，不强行
插入 token。这样 shared Family digest 不随 user 改变，现有 host-wide exact route manifest 和
AgentControl admission 可以继续服务多个不同角色；只有 per-session Definitions/Recipes 随角色名变化。

## 6. RecapGrid V6 参数化资产

建议新增 `galatea-rolling-rewrite-zh-cn-v6`，其 bundle factory 显式接收 validated character
parameters。概念 API：

```csharp
TryCreateRegistrationBundle(
    string assetId,
    GalateaRecapGridAssetParameters parameters,
    out RecapGridControlRegistrationBundle? bundle
)
```

物化顺序固定为：

1. 读取并验证 embedded source resources；
2. 用 shared renderer 展开两个 member prompts；
3. 用同一角色名构造 topic 与 semantic heading，保留稳定 block keys；
4. 构造 canonical Family/Definitions/bundle；
5. 在任何 repository/provider side effect 前完成全部 byte-bound 与 deterministic checks。

V6 的 shared family prompt 保持角色无关，因此不同 character name 应满足：

- Family digest、capability、route key、admission/profile 可相同；
- 两个 Definition digests 与 registration command digest 必须不同；
- logical column IDs、carrier 顺序和 runtime protocol 保持相同；
- 同一参数在任何 culture/process 中生成 exact 相同 canonical bytes。

另加一个重要 golden：V6 source template 以 `characterName:"Galatea"` 渲染时，finalized family、
topics、member prompts、headings、block keys 与 V5 exact 相同，因而 Family/Definition/registration
command digests 也相同。Asset selector 升到 V6 本身不进入 canonical bundle；仍叫 Galatea 的现有
session 不应被迫重建 Cells。只有角色名实际不同，Definitions/Recipe/EvaluationKeys/Cells 才旋转。

### 6.1 Operator CLI

`recap-grid scaffold` 与 `recap-grid control provision-asset` 对 V6 都要求 exact
`--character-name`与`--player-name`。不能 scaffold 一对名字、provision 时再隐式使用其他值。

实施后保持现有 provision operation/runtime identity与receipt合同，不把角色名或
`CanonicalCommandDigest`加入第二套identity。同一Control instance中用不同名字再次provision会复用现有V6
operation key并fail closed为`operation-conflict`；这与“首版不支持existing-session rename”的边界一致。

没有新增`asset describe`。现有provider-free `scaffold`输出已报告ordered Definition digests、targets与
canonical command digest；Control inspect/export则提供已激活状态的authority。为一个非必需便捷入口增加
第二套catalog surface与维护路径，不值得。

运行手册从 root config exact 读取名字并显式传给 CLI，例如：

```bash
character_name="$(jq -er --arg user "$user_id" \
  '.users[] | select(.userId == $user) | .characterName' "$galatea_config")"
player_name="$(jq -er --arg user "$user_id" \
  '.users[] | select(.userId == $user) | .playerName' "$galatea_config")"

dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid control provision-asset \
  --input "$session_repo" --branch main --confirm-ref "$ref_id" \
  --admission "$admission" \
  --asset galatea-rolling-rewrite-zh-cn-v6 \
  --character-name "$character_name" \
  --player-name "$player_name"
```

CLI 与 config 仍是两个 operator 入口，因此仅靠 runbook 无法消灭 copy drift。Galatea fresh/current
admission 使用该user的`characterName`重新计算expected V6 ordered Definition digests，并只读核对active
recipe。它在mandatory reply/extraction settlement之后、normalization/cutoff之前先做preflight；背景pre-setup与
`OpenFreshAsync`再次检查，在current Recap/main provider effect或SessionJournal setup write前fail closed。
failed-turn cleanup完成后，`PrepareFreshTurnAdmissionAsync`于真正cutoff前复检：

- no-active/raw-only 状态继续合法；
- active V6 必须 exact 是 expected world + autobiography Definitions；
- V5 exact Definitions若与 `characterName:"Galatea"` 的 V6 expected descriptor 相同，应视为 exact
  semantic match；错误名字或混合 Definition 必须返回稳定的 migration/mismatch classification，不得
  继续 maintenance 或 main Agent request；
- 同一个 typed alignment inspector 也必须服务 recent/readiness read-side；mismatch时返回稳定
  invalid/migration-required readiness，并把 Context header留空，不能继续展示旧名字的 recap；
- 该检查不能通过解析 prompt 文本、heading 或错误消息完成，只比较 typed/canonical identities。

实现收口：Galatea composition按user在`GalateaDelegationSupervisor`构造前从V6 bundle派生一次
`GalateaRecapGridTargetExpectation`，session只缓存`BuildTargetDigest`；
`GalateaRecapGridTargetInspector`直接使用正式Control reader/snapshot比较
`ActiveRecipe.Recipe.Target.Digest`。fresh admission、后台pre-setup、`OpenFreshAsync`与NewRequest均fail closed；
FrozenCompletionRequired不检查current target；ToolContinuationRequired在frozen tools全部settle后、current Online前检查。
readiness mismatch的exact分类为`state=invalid`、`code=character-asset-mismatch`且Context header为空。
首个release不支持existing-session rename，也不引入alias/transition asset或第二套prompt/heading descriptor。

这会要求 `Galatea.Server` 在 product composition 层引用 `Galatea.RecapGrid`，但不会反转后者当前的窄
依赖边界。检查属于 fresh/current policy；frozen 例外见 §8.3。

## 7. 邮箱、extractor 与 generated Observation

### 7.1 Per-user OutboundMailExtractor

推荐把 production composition 从 host-wide `IOutboundMailExtractor` 实例改成 host-wide factory +
per-user immutable extractor：

```text
shared Completion connection/client owner
        |
        +-- extractor factory -- characterName=Galatea -> extractor A
        |
        +-- extractor factory -- characterName=Alice   -> extractor B

UserSessionHost A/B 各自持有对应 extractor/reconciler；底层 borrowed Completion client仍可共享。
```

tool 的 JSON property names/schema 保持稳定，Descriptions 改为角色中立措辞；system/user source prompt
使用同一个 `${characterName}` renderer，明确 exact `[${characterName}]` marker。不要为每个 user
创建独立 connection registry 或 provider client owner。

所有 user 的 name validation、prompt render、`TextExtractor` construction 与 ContractId 计算都必须在
`GalateaDelegationSupervisor` 构造前完成。Supervisor 构造成功后 existing durable outbox 已可能立即 pulse，
其后不能再留下会失败的 composition preflight。

V2 extractor `ContractId` 必须包含 code-owned contract version 和 rendered semantic fingerprint，具体对
exact system/user prompt、tool/schema contract version 与 visible-action renderer
version 做 canonical SHA-256。角色名已经进入 rendered prompt，因此不同角色自然得到不同 ContractId；
connection/model 仍是执行路由，不必冒充 semantic contract。`IOutboundMailExtractor.ContractId` 应成为
required instance member，不再默认指向 production v1 constant。

已有 Action capture 是 settled durable fact，不因 current `characterName` 改变而重新提取。Existing capture 的
first committed batch继续为authority；即使它记录historical ContractId，也只核对frozen visible Action
identity并zero-call返回，不按current name重算。首版直接不支持existing-session rename，因此不为
想象中的rename gap引入额外coordinator或No-Go scanner；未来若真正开放改名，再把这一durable
boundary纳入独立迁移设计。

### 7.2 Inbound mail 与 reply notice envelope

Inbound 收件人与 reply notice 的生命周期不同，应分别处理：

- `MailboxMessage.To`改为消息自身冻结的validated character name。Authenticated inbound endpoint从
  `session.User.CharacterName`传入，HTTP caller仍不能自报`to`。保持原XML envelope shape，不增加无必要的
  `v="2"`。
- Inbound `TryUnwrap`不读取current config；它从已存XML读取并校验`to`，然后使用同一writer
  exact round-trip。这同时接受historical `to="Galatea"`和其他validated character name，无需版本分支。
- Codex reply 与 delivery-failure 的新 headings 改为不依赖姓名的“来自外界代行者 Codex 的回信”与
  “发往外界代行者 Codex 的信未能送达”。Main system prompt 已经定义当前角色是谁，无需在每个 runtime
  envelope 复制名字。
- Player-turn Observation保持原prefix、info-string grammar和delegation store schema V1。新writer写中立headings；
  reader只接受“当前中立headings”或“historical Galatea headings”这两种完整closed dialect，拒绝混用。
  不为同一字节grammar增加虚构的envelope V2/prefix。
- `ObservationBound` / `ObservationCommitted`的lease已持久化exact rendered bytes、UTF-8 length与SHA-256；
  cold reopen解析stored dialect并核对字段，不用current writer重算historical bytes。`CutoffFrozen`尚未
  冻结rendered Observation，因此沿用已有rollback/retry语义，无需renderer-version列或schema migration。
- Frozen Prepared request 已保存 exact Observation；恢复时不重新 wrap 成current dialect。

这样 mailbox transport 的 machine contract 与角色显示名解耦，只有真正需要识别 Action voice marker 的
extractor 才按 per-user 名称参数化。

## 8. Durable、rename 与 recovery 语义

### 8.1 新 session 与普通 prompt 编辑

- `create-if-missing` 使用已经渲染的 SystemPrompt 创建 exact 三个 setup events。
- existing Idle session 在下一次 fresh turn 由 `ReconcileDesiredSetup` 比较 finalized prompt；模板或名字改变
  时追加新的 `SystemPromptSetup`，不覆盖历史 setup。
- 完成首次 V4/exact-marker setup 后，相同 source template + 相同 character name 必须得到逐字相同 prompt，
  避免后续无意义 setup churn。

### 8.2 角色改名

改 `characterName` 同时改变 voice marker、主 prompt、extractor contract、两个 Recap Definitions 与未来
Context heading，所以它不是 cosmetic setting。更重要的是，旧 raw history 中的 `[旧名字]` 不会自动成为
`[新名字]` 的第一人称证据。首版应支持“不同 user 从各自 session 起点使用不同名字”，但**不承诺 existing
session 原地 rename**。

现有角色仍叫 `Galatea` 时，V6 Recap bundle 应与 V5 byte-identical，不重建 Cells；但主 prompt 会把
`[角色名]` 收紧为 exact `[Galatea]`，下一次 fresh turn应通过 `ReconcileDesiredSetup` 追加一个新的
`SystemPromptSetup`。若要给已有 session 真正改名，应另行设计 bounded historical marker aliases 或
transitional asset；如果本质是新角色，则使用新的 `sessionDir`、`delegationStateDir` 与 derived state。
不能只改 config 后把旧角色自传挂到新角色名下。

### 8.3 Frozen recovery

Recovery 必须按实际状态拆开，不能用一个 blanket bypass：

- `FrozenCompletionRequired` 的 main Completion request/system prompt已经 durable freeze；它全程按旧 exact
  identity恢复，不读取 current `characterName` 重渲染，也不受 current Definition gate阻断。
- `ToolContinuationRequired` 只有执行冻结 tool 到 durable ToolResult boundary 的前半段按旧 tool/runtime
  identity恢复；随后代码会重新打开 current RecapGrid Online。进入 `CatchUpMaintenanceAsync` 或 main new
  request 前，必须执行 current character/Definition gate，mismatch时停在已结算 tool boundary。
- Recap maintainer provider call 本身没有 SessionJournal Prepared/Started 形式的 durable frozen request；
  不应虚构“outcome-unknown maintainer work”来绕过 current gate。未提交的 derived call仍服从 RecapGrid
  自身现有可重试/重建语义。

因此 gate 不能作为无条件 exception 塞进 `CreateSessionAsync`，也不能只放在普通 fresh send。完成必要的
冻结恢复、outbound capture/reply lease settlement并回到 Idle 后，operator才能开始另行批准的 rename
migration。

## 9. V5 -> V6 migration

现有 V5 Family/Definitions/Cells/Recipe 都是 immutable derived artifacts，不手工改 key、prompt、heading、
digest 或 SQLite。迁移分两类：

### 9.1 角色仍叫 `Galatea`

V6 以 `characterName:"Galatea"` 渲染后必须与 V5 canonical bundle exact 相同。Operator 停服并检查
process/file lock、raw execution boundary、outbound capture/reply lease、Control/Timeline/Store health，
备份 V3 config 与 ignored source prompt 后，在provider-free bundle构造与Control inspect/export中核对：

- Family、两个 Definitions、registration command 与 active Recipe target digests全部 exact 命中；
- Recap source prompt token渲染回 V5 当前文字；root V4 main prompt则预期因 exact `[Galatea]` marker
  发生一次 `SystemPromptSetup` 更新；
- route/admission/profile不变，zero provider call、zero derived write。

满足这些 gate 后只需迁 V4 config/source files并启动新 binary；不创建 Recap candidate、不重建 Cells、
不 promote，也不为已是exact target的Control制造新provision receipt。第一次 fresh turn允许 exact 一次
main `SystemPromptSetup` durable write；这不等于 Recap asset重建，也不能被误报成zero-write root migration。

### 9.2 新 session 从一开始使用其他名字

用目标名字 scaffold/provision V6，再按现有正式流程创建 Full recipe、bounded build、zero-call proof
与 activation。因为它是新的 per-user repository，不存在旧 `[Galatea]` history 或 V5 content 继承问题。

existing session 的真正 rename 不在首版承诺内；若未来批准 transitional alias 方案，再以独立 candidate
Recipe 走 world-first build、materialization 检查、exact raw-head fence与原子 promotion，并保留旧 V5
artifacts作为 rollback evidence。

若任一步不确定，重新读取 Control/Store/Timeline fresh state；不要盲目重试 provider call或直接编辑
derived SQLite。Rollback 使用备份的 V3 config + prior binary/commit + retained V5 recipe，不能在 V4 reader
中留下默认 Galatea 的兼容分支。若 raw history 已经写入新 marker，简单回滚旧 prompt/recipe会漏读角色
证据，只能 forward-fix或使用明确的 transitional asset。

本轮已在停服、Idle边界下迁移ignored development instance：`cyber`与`gpt`均显式配置
`characterName:"Galatea"`，三份machine-local prompt改为`${characterName}` template并删除固定中文别名。
备份保留在`.atelia/galatea/migrations/2026-08-28-character-name-v4/`。迁移后的loopback、authenticated、
read-only canary已验证两个user能加载，cyber active recipe仍exact命中原target，gpt仍为raw-only；
未发送turn、未调用provider、未改写raw SessionJournal或Recap derived state。首次可写session attach只为gpt
创建了空delegation baseline，无capture/mail/notice/lease。

## 10. 已完成的实施工作包与验证矩阵

### WP-A：共享角色名/模板合同 + root config V4

- 新增窄 `Galatea.Prompts` assembly；
- 拆 file DTO 与 runtime config；hard-cut V4 字段；
- 更新 bootstrap、README、current V4 contract；
- 测 missing/null/type/wrong-case/duplicate、UTF-8 byte bound、NFC、delimiter、reserved label、inline/file
  precedence、unknown/malformed token、non-recursive render 和 rendered-size preflight；
- 测两个 user 共用同一 source file但得到不同 finalized prompts。

### WP-B：主会话 source templates 与 durable setup

- 把 active machine-local prompt 迁为 `${characterName}`，并保留带SHA-256核对的V3备份；
- 把当时tracked host template改成明确的template example，并处理旧English prompt的current/archive状态；
- 测 brand-new bootstrap 保存 rendered prompt、existing Idle 只在最终文本变化时追加 setup；
- 测 Prepared/frozen recovery 仍使用旧 exact setup，不受 current template/config 重渲染。

### WP-C：RecapGrid V6 asset + parameterized CLI

- member resource、topic、heading 参数化；Family、block keys保持角色无关/稳定；
- asset catalog/CLI 强制 `--character-name` + `--player-name`，保持原receipt/runtime identity；
  不增加describe-only surface；
- golden tests 锁定 `Galatea` 对 V5 byte-identical、异名 shared Family/split Definitions、resource
  exactness、无未展开 token、无角色意义上的 literal `Galatea`。

### WP-D：Mailbox/extractor identity 收口

- per-user extractor factory/rendered prompt/semantic ContractId；共享 underlying Completion client；
- outbound capture 测同user deterministic、不同名字分离、已有capture不重做；rename留在首版边界外；
- inbound保持原XML shape并冻结exact `To`；reply observation在同prefix下使用角色中立headings；
  reader exact支持current/legacy两种dialect并拒绝混用；
- 覆盖`CutoffFrozen`回滚语义、bound/committed exact stored Observation的cold reopen、recent display与
  Undo/reconciliation；不增加renderer version或store schema列。

### WP-E：Galatea fresh/current compatibility gate

- typed 比较 current user 期望的 ordered V6 Definition digests 与 active recipe；
- 覆盖 raw-only、V5-as-Galatea exact match、wrong-name、mixed definitions、raw-head stale、unsupported outcome
  与mismatch稳定错误分类；不为不稳定的file-busy情景增加脆弱fixture；
- 独立覆盖 `FrozenCompletionRequired` 全程 bypass、`NewRequestRequired` current gate，以及
  `ToolContinuationRequired` 的“冻结 tool 前半段 + current Recap 后半段 gate”。

### WP-F：operator migration

- 停服并核对Idle raw heads、active recipe、delegation gap/lease、进程与file holders；
- 备份V3 config/source prompt，迁移ignored development instance为V4/Galatea exact rendering；
- 用authenticated read-only loopback canary验证加载、readiness、active/raw-only状态与zero provider call；
- 迁移不触发candidate build、promotion或derived SQLite修改，因为Galatea V6 target与原V5 exact相同。

## 11. 不采用的方案

- **全局替换 literal `Galatea`**：会误改 product/binding/asset identity，也无法处理 marker delimiter、代词、
  heading、digest 与 historical text。
- **用 `userId` 充当角色名**：authentication/session identity 与故事角色 identity 不是同一概念。
- **只模板化主 system prompt**：Recap maintainer 与 outbound extractor 仍会按 `[Galatea]` 判定角色
  证据，generated mail Observation 也仍称呼 Galatea，产生最危险的 silent semantic split。
- **在 provider dispatch 时替换 Recap prompt**：最终请求与 canonical Definition/EvaluationKey 不一致，
  破坏 replay 和 Cell reuse。
- **让每个 user 生成不同 Family**：没有必要；会使当前 host-wide route manifest/profile 也按名字扩张。
  名字只应进入 member Definition。
- **缺字段时默认 `Galatea` 或双读 V3/V4**：会掩盖未迁移 user；项目尚未发布，应 hard cut。
- **引入通用模板引擎**：当前只有一个可信 operator variable，表达式、include、条件、escape 模式只会增加
  prompt supply-chain 与 determinism 风险。

## 12. 实施后不可回退的合同闸门

后续修改应把以下主张视为同一个闭环，不能只保留其中一半：

- V4 config 的 `characterName` 是唯一 current authority；
- 主 system prompt 和 V6 Definitions 都由同一 validator/renderer 物化；
- Recap family 保持角色无关，member Definition 承担角色语义；
- Outbound extractor 按 user 参数化并旋转 semantic ContractId，mail/reply envelope 不再写死角色名；
- 同一 user 的 finalized main prompt、Recap member prompts 与 outbound extractor都必须使用同一个 exact
  `[characterName]` marker；
- active recipe 与 current user 角色名在 fresh/current 路径 fail-closed 核对；
- frozen recovery 与旧 immutable derived artifacts不被 current config 重写；
- README、root current contract、operator runbook、prompt resources 与 tests 同步更新。

实施已通过正式Control reader/snapshot完成typed target inspection，没有解析prompt/heading文本，
也没有为此扩张RecapGrid public surface。后续不得把mismatch gate降级为文本推断或错误消息解析。
