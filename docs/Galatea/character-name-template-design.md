# Galatea per-user 角色名模板化设计

状态：**Accepted design baseline；已获准实施，current product contract 仍以代码与版本化合同为准**
日期：2026-08-28
范围：`prototypes/Galatea` 主会话 system prompt、mail/extractor generated prompts、
`prototypes/Galatea.RecapGrid` code-owned rolling recap asset，以及它们与 per-user
`config.json` 的一致性。

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

## 2. 已验证的现状与边界

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
- [`docs/Galatea/prompt/trpg-host.md`](prompt/trpg-host.md) 是 tracked prompt 文档，但当前没有
  runtime loader 引用；它适合作为 source template 示例，不能被描述成当前 host 的自动真源。
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

选择 `${...}` 而不是 `{{...}}`，是为了不与现有 `trpg-host.md` 中用于 memory slots 的 `{{}}`
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
- 拒绝 control/format/line/paragraph separator，以及会破坏 voice/template grammar 的
  `[`、`]`、`$`、`{`、`}`；
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

`recap-grid scaffold` 与 `recap-grid control provision-asset` 应对 V6 都要求 exact
`--character-name`。不能 scaffold 一个名字、provision 时再隐式使用 `Galatea`。

当前 provision receipt identity 只包含 `controlInstanceId + assetId`。参数化后必须加入 bundle 的
`CanonicalCommandDigest`，并把 operation/runtime identity 升版；否则同一 V6 asset 用不同名字再次
provision 会命中同一个 operation key 并形成 conflict。复用 Control-owned command digest 比另造一套
parameter codec 更直接，也不会把任意 Unicode 名字拼进 operation ID。

还应补一个 provider-free `asset describe`（或等价的 scaffold describe-only 形状），输出该参数下的
ordered Definition digests、targets 与 canonical command digest。这样多 user 共用一份 host-level
route/profile 时，不必为每个 user 重复创建相同 scaffold 文件，只为取得各自 Definition digests。

运行手册从 root config exact 读取名字并显式传给 CLI，例如：

```bash
character_name="$(jq -er --arg user "$user_id" \
  '.users[] | select(.userId == $user) | .characterName' "$galatea_config")"

dotnet run --project prototypes/SessionJournal.Cli -- \
  recap-grid control provision-asset \
  --input "$session_repo" --branch main --confirm-ref "$ref_id" \
  --admission "$admission" \
  --asset galatea-rolling-rewrite-zh-cn-v6 \
  --character-name "$character_name"
```

CLI 与 config 仍是两个 operator 入口，因此仅靠 runbook 无法消灭 copy drift。Galatea fresh/current
admission 应在 `ReconcileDesiredSetup`、任何 SessionJournal write 及 maintainer/provider effect **之前**，用该
user 的 `characterName` 重新计算 expected V6 ordered Definition digests，并只读核对 active recipe：

- no-active/raw-only 状态继续合法；
- active V6 必须 exact 是 expected world + autobiography Definitions；
- V5 exact Definitions若与 `characterName:"Galatea"` 的 V6 expected descriptor 相同，应视为 exact
  semantic match；错误名字或混合 Definition 必须返回稳定的 migration/mismatch classification，不得
  继续 maintenance 或 main Agent request；
- 同一个 typed alignment inspector 也必须服务 recent/readiness read-side；mismatch时返回稳定
  invalid/migration-required readiness，并把 Context header留空，不能继续展示旧名字的 recap；
- 该检查不能通过解析 prompt 文本、heading 或错误消息完成，只比较 typed/canonical identities。

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

V2 extractor `ContractId` 必须包含 code-owned contract version 和 rendered semantic fingerprint，例如对
canonical character name、exact system/user prompt、tool/schema contract version 与 visible-action renderer
version 做 canonical SHA-256。角色名已经进入 rendered prompt，因此不同角色自然得到不同 ContractId；
connection/model 仍是执行路由，不必冒充 semantic contract。`IOutboundMailExtractor.ContractId` 应成为
required instance member，不再默认指向 production v1 constant。

已有 Action capture 是 settled durable fact，不因 current `characterName` 改变而重新提取。反过来，角色
rename 前必须证明所有已完成 terminal Actions 都已经 capture，且不存在正在结算的 extraction gap；否则
crash 后尚未 capture 的旧 Action 会被 current extractor 解释。V4 migration runbook 应把这一项和
Prepared/OutcomeUnknown 检查列为同级 No-Go gate。Existing capture 的 first committed batch 继续为 authority；
即使它记录的是 historical ContractId，也只核对 frozen visible Action identity并 zero-call 返回，不按 current
name重算。

### 7.2 Inbound mail 与 reply notice envelope

Inbound 收件人与 reply notice 的生命周期不同，应分别处理：

- `MailboxMessage.To` 从当前计算属性改为消息自身冻结的 validated character name。Authenticated inbound
  endpoint从 `session.User.CharacterName` 传入，HTTP caller仍不能自报 `to`；new v2 envelope 写 exact
  `v="2"` 与 `to="<characterName>"`。
- Inbound `TryUnwrap` 不读取 current config：v1 继续 exact round-trip既有 `to="Galatea"`，v2 从 envelope
  读取、校验并冻结 `To` 后再 exact round-trip。这样历史 raw mail不会在改配置后变成 opaque，也不需要改写。
- Codex reply 与 delivery-failure 的新 headings 改为不依赖姓名的“来自外界代行者 Codex 的回信”与
  “发往外界代行者 Codex 的信未能送达”。Main system prompt 已经定义当前角色是谁，无需在每个 runtime
  envelope 复制名字。
- Ready composite V2 必须使用新的 exact prefix/version marker（例如固定
  `schema=atelia.galatea.player-observation.v2`），不能只靠“标题文字刚好不同”猜版本。V1 branch锁定当前
  prefix + Galatea headings，V2 branch锁定新 prefix + neutral headings + 既有 info-string grammar。
- 新 writer 只写 v2。Recent display/audit reader 必须继续 exact 识别已经持久化的 v1 Galatea envelopes
  和新 v2；这是 immutable raw history 的 versioned reader，不是 current write compatibility fallback。
  两个 reader branch 都要 closed/exact：v1 只接受无版本 + `to="Galatea"` 的旧 shape，v2 只接受
  `v="2"` + validated frozen character name，不能放宽成“任意 to/heading 都可信”。
- reply lease 当前会重新计算 canonical rendered Observation；renderer contract/version必须在 lease
  membership冻结进入 `CutoffFrozen` 时就 durable确定，并贯穿 `ObservationBound` / `ObservationCommitted`
  reopen validation。也可在停服迁移时证明没有 active/leased notice后 hard-cut store schema；不能让新
  静态 heading把旧 lease误报为 corruption，或让尚未 bind Observation 的旧 cutoff被新 renderer重写。
- Frozen Prepared request 已保存 exact Observation；恢复时不重新 wrap 成 v2。

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
备份 V3 config 与 ignored source prompt 后，执行 provider-free V6 describe：

- Family、两个 Definitions、registration command 与 active Recipe target digests全部 exact 命中；
- Recap source prompt token渲染回 V5 当前文字；root V4 main prompt则预期因 exact `[Galatea]` marker
  发生一次 `SystemPromptSetup` 更新；
- route/admission/profile不变，zero provider call、zero derived write。

满足这些 gate 后只需迁 V4 config/source files并启动新 binary；不创建 Recap candidate、不重建 Cells、
不 promote。第一次 fresh turn允许 exact 一次 main `SystemPromptSetup` durable write；这不等于 Recap asset
重建，也不能被误报成 zero-write root migration。
可以补一条 V6 parameterized provision receipt作 operator audit，但它不能成为“语义已经迁移”的替代证据。

### 9.2 新 session 从一开始使用其他名字

用目标名字 scaffold/describe/provision V6，再按现有正式流程创建 Full recipe、bounded build、zero-call proof
与 activation。因为它是新的 per-user repository，不存在旧 `[Galatea]` history 或 V5 content 继承问题。

existing session 的真正 rename 不在首版承诺内；若未来批准 transitional alias 方案，再以独立 candidate
Recipe 走 world-first build、materialization 检查、exact raw-head fence与原子 promotion，并保留旧 V5
artifacts作为 rollback evidence。

若任一步不确定，重新读取 Control/Store/Timeline fresh state；不要盲目重试 provider call或直接编辑
derived SQLite。Rollback 使用备份的 V3 config + prior binary/commit + retained V5 recipe，不能在 V4 reader
中留下默认 Galatea 的兼容分支。若 raw history 已经写入新 marker，简单回滚旧 prompt/recipe会漏读角色
证据，只能 forward-fix或使用明确的 transitional asset。

本设计不授权当前研究轮次修改 ignored live config、prompt 或 live RecapGrid repository；真实迁移应是
独立的 operator 工作包。

## 10. 推荐实施工作包与验证矩阵

### WP-A：共享角色名/模板合同 + root config V4

- 新增窄 `Galatea.Prompts` assembly；
- 拆 file DTO 与 runtime config；hard-cut V4 字段；
- 更新 bootstrap、README、current V4 contract；
- 测 missing/null/type/wrong-case/duplicate、UTF-8 byte bound、NFC、delimiter、reserved label、inline/file
  precedence、unknown/malformed token、non-recursive render 和 rendered-size preflight；
- 测两个 user 共用同一 source file但得到不同 finalized prompts。

### WP-B：主会话 source templates 与 durable setup

- 把 active machine-local prompt 迁为 `${characterName}`（真实 ignored instance 另行授权/执行）；
- 把 tracked `trpg-host.md` 改成明确的 template example，并处理旧 English prompt 的 current/archive 状态；
- 测 brand-new bootstrap 保存 rendered prompt、existing Idle 只在最终文本变化时追加 setup；
- 测 Prepared/frozen recovery 仍使用旧 exact setup，不受 current template/config 重渲染。

### WP-C：RecapGrid V6 asset + parameterized CLI

- member resource、topic、heading 参数化；Family、block keys保持角色无关/稳定；
- asset catalog/CLI 强制 `--character-name`，receipt 加 canonical command digest；
- 增加 describe-only operator surface；
- golden tests 锁定 `Galatea` 对 V5 byte-identical、异名 shared Family/split Definitions、resource
  exactness、无未展开 token、无角色意义上的 literal `Galatea`。

### WP-D：Mailbox/extractor identity 收口

- per-user extractor factory/rendered prompt/semantic ContractId；共享 underlying Completion client；
- outbound capture 测同 user deterministic、不同名字分离、已有 capture 不重做、rename 前 gap No-Go；
- inbound v2 冻结 exact `To`；reply observation v2 使用角色中立 headings；二者保留 exact v1 historical reader；
- 覆盖 reply lease renderer version在 `CutoffFrozen`、`ObservationBound`、`ObservationCommitted` 三阶段的
  cold reopen、frozen exact Observation、recent display 与 Undo/reconciliation。

### WP-E：Galatea fresh/current compatibility gate

- typed 比较 current user 期望的 ordered V6 Definition digests 与 active recipe；
- 覆盖 raw-only、V5-as-Galatea exact match、wrong-name、mixed definitions、busy/stale/unsupported 与稳定错误分类；
- 独立覆盖 `FrozenCompletionRequired` 全程 bypass、`NewRequestRequired` current gate，以及
  `ToolContinuationRequired` 的“冻结 tool 前半段 + current Recap 后半段 gate”。

### WP-F：operator migration

- 先用隔离 fixture/clone 演练 candidate build + atomic promotion + rollback；
- 再经用户明确授权迁移 ignored development instance；
- 记录 exact commit、raw head、old/new recipe、call cap、zero-call proof、final health 与进程/lock cleanup。

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

## 12. 实施前最终闸门

进入代码实施前，应把以下主张视为同一个闭环，不能只完成其中一半：

- V4 config 的 `characterName` 是唯一 current authority；
- 主 system prompt 和 V6 Definitions 都由同一 validator/renderer 物化；
- Recap family 保持角色无关，member Definition 承担角色语义；
- Outbound extractor 按 user 参数化并旋转 semantic ContractId，mail/reply envelope 不再写死角色名；
- 同一 user 的 finalized main prompt、Recap member prompts 与 outbound extractor都必须使用同一个 exact
  `[characterName]` marker；
- active recipe 与 current user 角色名在 fresh/current 路径 fail-closed 核对；
- frozen recovery 与旧 immutable derived artifacts不被 current config 重写；
- README、root current contract、operator runbook、prompt resources 与 tests 同步更新。

如果实现阶段发现 active recipe 的 typed inspection 无法在不扩张 RecapGrid public surface 的前提下完成，
应先补一个最小 read-only descriptor/view，而不是退回解析 prompt/heading 文本或取消 mismatch gate。
