# DerivedRecap Grid WP-07C：Agent Control 与 Tool Continuation

状态：Complete；两路independent closure GO；current production仍待WP-08 atomic cutover

只需加载：目标设计、Master、WP-07B handoff、本文与WP-08摘要。

## Intent

在不切production default的前提下，给WP-07B disposable Online composition补齐Agent-facing Control capability、code-owned genesis
入口与ToolResult/ToolContinuation lifecycle。WP-07C不重写Manager/Runtime scheduler，不持久化promotion proof，也不把operator fixture
冒充Agent tool。

## Implementation plan-lock（2026-08-11）

本工作包按以下authority边界施工；这些裁决覆盖下方较早的概述性措辞：

- `ToolExecutionStarted`只冻结同一`operationId`、reserved execution sequence与
  `SessionToolRuntimeIdentity`。恢复后物理tool handler仍可能重复进入；SessionJournal提供的是稳定去重/查询键，
  **不是exactly-once**。只有天然幂等或backend能按operation receipt精确结算的tool可以自动恢复。
- Control state升级为strict schema V2。每个成功Agent mutation把bounded terminal operation receipt与语义变更放在
  同一次whole-state publish中；receipt提交operation-id派生键、sequence、runtime-identity digest、由Control计算的
  canonical command digest、原始result identity及原instance/generation。receipt不保存whole next head；replay返回当前strict
  `ControlHeadRef`，并分别报告`HeadAdvancedSinceApply`与`InstanceReplaced`。same operation+same command只返回
  `Replayed`且零写/零generation；same operation+different command返回`Conflict`。receipt lookup在writer lock内先于
  stale-head判断，最多16,384条；V1明确`UnsupportedSchema`，不静默迁移。
- Agent-facing唯一tool为`recap_grid.control`。payload只允许`action`与该action所需的
  `canonicalValueBase64`、`builtInAssetId`或`recipeDigest`；不接受Control/Timeline authority。mutation所需whole heads
  由owner-bound Control/Timeline handle在operation开始时捕获，Control内部生成command digest。
- registration bundle与promotion分别是单次Control transaction。promotion对current Timeline head pure-read执行
  `InspectBuildProgress(ExplicitCandidate)`，只接受`Complete + FulfillmentPresent + exact RecapGridPromotableProof`；不调用Build或写Store；partial receipt、missing、stale
  均不得activate。proof不编码、不落盘。
- lifecycle trigger显式区分`PreObservation`、`ObservationAccepted`、`ToolResultObserved`。只有
  `PreObservation`在无既存Grid debt时允许Online reconcile/seal；后两者可逐pass恢复既存Grid debt但绝不seal，当前Observation/ToolResult必须保留在
  SessionJournal-owned raw tail。empty Timeline上的ToolResult同样走raw bootstrap，不得被active recipe阻塞。
- CLI/Galatea candidate恢复必须exact bind frozen tool identity/profile。Prepared/Completion Started保持frozen并且对
  Online、Control、Timeline和current route零触达；Tool continuation使用frozen profile与同一operation/sequence，
  settled ToolResult后的新completion才可选择current completion/profile。Started Refuse仍位于所有candidate owner之前。
- code-owned built-in assets只通过显式operator provision入口安装；normal Host绝不隐式Create。current production、
  public Galatea constructor/DI与old DerivedRecap路径保持不变。

施工顺序固定为Control V2 receipts -> AgentControl -> Completion.Tools/lifecycle -> candidate Hosts ->
public/architecture/docs gates。每个切片先focused green，再进入下一层。

## Locked scope

- 定义唯一Agent-facing provider-neutral control tool/capability，显式覆盖Family、Definition、Recipe登记与candidate promote；输入只接受正式
  canonical values或code-owned built-in asset ID，全部经过Control admission/allowlist/cap/budget复验；
- code-owned built-in genesis必须由明确operator命令生成canonical assets并显式provision；normal Galatea/CLI Host缺Timeline/Control/Store时
  fail closed，绝不auto-create；
- ToolResultObserved/ToolExecutionStarted恢复沿SessionJournal existing operation ID/sequence authority；composite lifecycle仅在
  `PreObservation`执行bounded reconcile/seal -> fulfill -> readiness；`ObservationAccepted`与`ToolResultObserved`只做readiness并
  把本次raw事件保留在tail。Prepared/Started保持frozen zero-active-derived；
- A/B candidate build与promotion分离：tool只提交Control transaction；需要promotion时fresh same-head proof由Manager pure-read检查，proof不编码、不落盘；
- Galatea与candidate CLI继续复用WP-07B Online+Hosting owner，不建立第二registry、scheduler、raw audit或Control backend；
- current production、old DerivedRecap projects与public Galatea constructor/DI保持不变，直到WP-08 atomic cut。

## Acceptance

1. built-in canonical assets golden、operator provision、normal missing-state fail closed；
2. Agent创建`XSuspicion` definition/overlay，unauthorized family/carrier/prefix/budget零mutation；
3. duplicate/replayed operation ID幂等，same expected Control CAS竞争仅一胜；
4. ToolExecutionStarted restart、ToolResultObserved多次safe lifecycle、caller cancel与raw/control/timeline drift；
5. Prepared/Started删除或损坏active Grid/control/routes仍按frozen bytes恢复，active collaborators调用数为零；
6. explicit candidate build -> pure-read progress/proof inspection -> promotion；partial/stale/missing无activate，Store零写、
   provider零构造/零dispatch；该pure-read proof会按authority读取Store，不能误写成Store零触达；
7. CLI与Galatea actual turn E2E，old v8 inert，public/architecture/source gates。

## No-Go

- normal Host auto-provision；
- tool payload自授权admission或provider route；
- durable campaign/proof/selection registry；
- Tool continuation复制SessionJournal reducer或跳过operation ID authority；
- WP-07C尚未GO就让WP-08开始production cutover。

## Handoff to WP-08

交付Agent control/tool continuation exact caller switch、built-in/operator assets、recovery矩阵与zero-old-owner删除清单。WP-08只有在
WP-07B与WP-07C都GO后才可开始。

## Implementation and closure record（2026-08-11）

- `SessionJournal.RecapGrid.Control` 已升级strict schema V2，并加入bounded terminal operation receipts、registration/promotion
  whole-state bundle、same-operation replay/conflict、restore union与reinitialize receipt preservation。receipt只提供at-least-once
  tool重入下的durable settlement/idempotency authority，**不把SessionJournal恢复宣称为exactly-once**。
- 新增provider-neutral `SessionJournal.RecapGrid.AgentControl`：唯一strict `recap_grid.control` tool、owner-bound lazy dependencies、
  immutable profile registry、code-owned built-in assets与同一asset resolver驱动的显式operator provision入口。tool parser显式
  `MaxDepth=8`；normal CLI/Galatea Host不auto-create Timeline/Control/Store。
- `Completion.Tools`显式透传unsettled/fatal execution；neutral lifecycle加入
  `PreObservation|ObservationAccepted|ToolResultObserved`，且只有PreObservation可seal。ToolResult仍保留在SessionJournal raw tail。
- Prepared先exact bind frozen completion与frozen tool identity，但AgentControl binding保持lazy、对active derived
  owners零触达；ToolContinuation先bind frozen tool profile，用exact operation/sequence逐个执行并settlepending tools：每次public
  primitive只推进一个durable operation并返回exact next head，Host按`MorePending`继续，直到dependency-closed
  `ToolResultObserved` boundary，再做bounded Online catch-up；只有Ready后才bind current completion并Resume。Started Refuse仍在最外层。
- dense fixture以真实`ToolSession`登记Family、`CulpritHypothesis`/`XSuspicion` definitions、full base与overlay recipe作为明确
  precondition；WP-06 Runtime实际补齐missing candidate。随后main scripted provider在真实CLI
  `run-online-turn` Host内发出`recap_grid.control` promotion ToolCall；同一Hostpure-read取得exact proof并提交
  receipt，recap provider call数不增加。对应ToolResult保留在raw tail，紧随其后的completion可见active candidate contribution；
  旧v8 sentinel保持逐字节inert。Galatea仍覆盖相同frozen recovery与authority-equivalence边界，不冒充第二条promotion fixture。
- 最终串行证据：Control 45/45、AgentControl 20/20、AgentControl external public surface 1/1、Completion 482/482、
  SessionJournal lifecycle/recovery targeted 7/7、Online 22/22、Hosting 19/19、CLI candidate 12/12、Galatea
  candidate + stop/lifecycle targeted 23/23、Online/Hosting public surfaces 3/3、Walking architecture 23/23；`Atelia.sln`
  build 0 warning / 0 error，vulnerable package scan零命中，docs 15/0，diff clean。两路independent closure均GO
  （P0=0，P1=0）；containing commit提供commit evidence。WP-08因此转为Ready，但current production仍未cutover。
