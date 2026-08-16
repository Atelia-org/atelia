# SessionJournal Contract Freeze R2 — operator cutover and HTTP/SSE plan lock

状态：CF-D-01 operator cutover complete；CF-D-02a/02b read-only review与plan lock complete；CF-D-02 R3未开始  
调查基线：`e1d785f0e29942ce698dcefe0392c5c535432b4d`  
日期：2026-08-16

本文记录 [Contract Freeze R2 active plan](../work/active/session-journal-contract-freeze-r2.md) 后续三项工作：

1. 实际 Galatea operator `connections.json` 的V1 cutover；
2. `CF-D-02a` HTTP core accepted-language与support-role调查；
3. `CF-D-02b` SSE event/state-machine调查。

HTTP/SSE本轮只读，没有修改production/tests、启动provider或改写真实journal。`Plan lock complete` 表示
Adopt/Retain/Prototype/Reject边界和前置blocker已锁定，不表示HTTP/SSE已实施或frozen。

## 1. 综合结论

1. **CF-D-01 operator cutover已完成。** live manifest现在是exact numeric `v:1`、5项env-only source，
   Completion V1 decoder和Galatea full config loader均在实际service env下provider-free通过；实际session为Idle、
   Prepared为0。独立review PASS。
2. **CF-D-02确有高收益direct cut。** 推荐server与cache-busted browser一起hard-cut到 `/api/v1`，删除旧route；
   HTTP采用endpoint-specific strict DTO与最小 `{code,error}`，SSE采用closed typed event union；在进程仍可正常
   执行的turn中建立exact terminal与linear publish invariant。拒绝generic HTTP/SSE/CLI envelope、dual routes和
   自研通用JSON parser。
3. **不能直接宣称V1 bounded/frozen。** `recent/pop/done.recent` 当前在取最新6 turns前先做whole-lineage
   unbounded/offline replay；单raw event logical payload又可接近256 MiB。只给response writer加cap不能解决此前的
   decode、string materialization和copy。`CF-D-02-P0 bounded recent projection` 因此是R3/freeze前置包。
4. **numeric request/response budgets仍是显式产品选择。** 仓内没有事实能推导“chat message应为1 MiB”；
   config/connections的相邻常数不构成依据。本轮只锁需要哪几层limit和oversize语义，不发明数字、分页或截断。

## 2. CF-D-01 operator cutover evidence

### 2.1 Preflight与唯一改写

- 没有Galatea进程；user/system service unit均未运行；
- live manifest是non-link regular file、current operator owner、mode `0600`、link count 1；
- cutover前实际selected session offline validation为150 events、execution phase `Idle`、
  `preparedRequestCount=0`，且report之后repository没有文件变化；
- 5个endpoint env与5个API-key env在当前service env中都存在、非空并通过4 KiB / 64 KiB cap；
- 唯一改写是增加root numeric `"v":1`，删除5个空 `baseAddress`；ID、default、kind、model、surface、
  env locator或secret均未修改、输出或记录。

### 2.2 Post-cutover gates

- exact root keys为 `v/connections/defaultConnectionId`，raw version token `1`；把内存mutation改成 `1.0`
  会由shared decoder fail closed；
- 5项均无inline `baseAddress/apiKey`，只保留nonblank env sources；default exact命中一项；
- `CompletionConnectionConfigLoader.LoadFile` 成功；
- `GalateaConfigLoader.Load` 成功加载1 user、5 connections与RecapGrid配置；没有materialize client或调用provider；
- 删除version并规范化空inline endpoint后的semantic SHA-256仍为
  `7c2f6d1dcb8e4a92ad4a1f68ea314f0f66a0b8c1960323c36d106b5c2646e361`，与pre-cutover一致；
- 独立reviewer重复上述content-free检查后给出PASS。

manifest是ignored operator state，不进入Git commit。rollback必须在停服时对code与manifest成对执行；当前没有
旧文件逐字节备份，inverse transformation可恢复旧语义，但不能声称byte-exact rollback artifact。

## 3. CF-D-02a — HTTP core current language

### 3.1 Endpoint与consumer

current `/api` group在
[`Program`](../../../prototypes/Galatea/Program.cs) 注册7个HTTP core endpoints和1个SSE endpoint：

| Endpoint | Success | Current error drift | First-party support role |
|:--|:--|:--|:--|
| `GET /me` | `{userId,maintenanceMode}` | 401 empty | operator/auth canary；browser不调用 |
| `GET /recent-turns` | `RecentTurnsResponseDto` | 401；503 `{code,error}` | browser history、rewind capability、同head RecapGrid readiness |
| `POST /chat/turns` | 202 `StartTurnResponseDto` | `{error}`、`{code,error}`、recovery四字段、busy response | browser只需success `turnId`；409只读 `turnId/error` |
| `POST /chat/turns/resume` | 同上 | 同上 | browser只需 `turnId/error` |
| `POST /chat/turns/pop-latest` | `{turn,recent}` | recovery/busy shape drift | browser消费turn + coherent recent |
| `GET /chat/turns/current` | `CurrentTurnDto` | 401；503 | browser只读status/turnId/connectionId/restartRequired/recoveryHead |
| `POST /chat/turns/{turnId}/stop` | `{status,turnId}` | 404 `{error}` | browser不读success body；失败读error |

tracked browser consumer在
[`galatea.js`](../../../prototypes/Galatea/wwwroot/assets/galatea.js)。HTML中的
`window.galateaBootstrap` 与同一次server deployment的cache-busted JS一起交付，不升级为独立stable network
API；本包只要求其随route hard cut原子更新。

### 3.2 Framework defaults不是contract

当前没有application-owned HTTP JSON options或ProblemDetails/error handler：

- request property case-insensitive、unknown ignored、duplicate last-wins；
- missing/null/required主要依赖constructor/default和少量post-binding检查；
- malformed/no-body/wrong-media-type由framework产生400/415，body受hosting environment影响；
- request body、message、normalized message和HTTP response都没有code-owned byte bound；
- non-explicit exception可能产生环境相关500 body。

这些是偶然accepted language，不能直接冻结为V1。

### 3.3 Locked V1 direction

#### Version与request decode

- `Adopt`：直接把完整group hard-cut到 `/api/v1`，旧 `/api/*` 返回404；不保留redirect、alias或dual route。
- `Adopt`：endpoint-specific request DTO；只接受 `application/json` object；exact camelCase；unknown、case
  variant、missing/non-null required、wrong type、explicit null required、comment、trailing comma拒绝。
- `Adopt`：known duplicate也拒绝。使用.NET 10现有
  `JsonSerializerOptions.AllowDuplicateProperties=false`，不为此发明token scanner。
- `Adopt`：`connectionId` 复用Completion owner的128 UTF-8 byte cap；EventAddress必须exact canonical；
  `turnId`必须32 lowercase hex。`connectionId` absent/null表示default，blank/whitespace与超cap一律拒绝。
- `Prototype-required-decision`：request-body、original message、normalizer output的numeric byte budgets。
  任何选定值都必须在normalization前后复验；normalizer自身也需要output/materialization bound。

可使用ASP.NET/System.Text.Json现有strict options与最外层API middleware；不写第二个JSON parser，不冻结
framework ProblemDetails或exception detail。maintenance middleware继续早于endpoint binding，但以endpoint metadata
只标记已知write routes，不能把未知 `/api/v1` path也拦成503。malformed/oversized已知write在maintenance mode仍应
得到稳定 `maintenance-mode`，且不打开session。通用异常映射排除request-aborted cancellation；response一旦started
便不得再追加JSON error。

browser必须对每个endpoint的success/error做小型local required/type/status validator；不能继续用
`payload?.turns ?? []` 一类默认值把malformed V1 success静默降级为空history，也不建立通用schema framework。
response duplicate由server exact-output fixtures保证不生成；browser不为同源producer再写raw JSON duplicate tokenizer，
只在`JSON.parse`后校验exact property set/type/status。

#### Success surface

- start/resume 202只返回 `{turnId}`；
- stop改204、无body；
- pop收窄为 `{poppedUserText}`；不再返回完整turn或recent。browser用authoritative popped text恢复draft，
  随后单独GET bounded recent；me保留canary shape；
- current只保留 `status,turnId,connectionId,restartRequired,recoveryHead`；`status`为closed set
  `idle|running|recovery-required|unprovisioned`；
- 删除current中未消费的 `userMessage/phase/durablePhase/recoveryRequired`，不把内部enum/phase冻结为HTTP；
- recent继续把turns、rewind token与RecapGrid readiness作为同一个captured-head coherent snapshot，拒绝拆endpoint。

nullable properties首版继续显式输出null，不顺手全局omit-null。JSON property order不属于semantic contract；tests
用exact property set/type检查，不让consumer依赖顺序。

#### Error surface

只定义两个小shape：

```text
ApiError      { code, error }
TurnBusyError { code:"turn-busy", error, turnId }
```

`turnId` 是required nullable `null|string`，保留“writer lock已占用但live turn尚未publish”的真实窗口；空string
不合法。所有其他failure都用
`{code,error}`；删除recovery error中重复的phase/head，browser可从current endpoint取得同一workflow authority。
`code`是machine branch；`error`必须nonblank但逐字文本只是human diagnostic。401/400/404/409/413/415/503/500
都由Galatea自有code映射，不返回HTML、ProblemDetails或exception detail。

## 4. CF-D-02-P0 — bounded recent projection blocker

### 4.1 Count不是work/byte bound

default EventJournal单event logical payload上限为：

```text
SizedPtr.MaxLength                         268,435,452
- RBF fixed overhead                               24
- EventFrameHeader fixed length                    64
= maximum logical payload                 268,435,364 bytes
```

Observation content、terminal Action text/reasoning没有更小的Galatea/SessionJournal display bound。六个最简
no-tool turns已至少有约12个large-payload-bearing events，tool loops还可继续增加raw events；final JSON escaping
也会继续放大。

更早的风险位于
[`SessionJournalEngine.CompletedTurns`](../../../prototypes/SessionJournal/SessionJournalEngine.CompletedTurns.cs)：
`ReadRecentCompletedTurns(6)` 先调用明确标为unbounded/offline的
`ReadHistoryPlanningWindowAt(root)`，materialize完整selected lineage和全部completed turns，最后才取6项。
Galatea随后用多个StringBuilder/filter再次复制display text。

受影响surface：recent、pop、SSE `done.recent`。`RewindLatestCompletedTurn`也复用全量定位，因此pop必须在ref
move之前证明最小receipt projection与encoding可成功；不能出现mutation成功、receipt serialization/limit失败。

### 4.2 Locked direction

`CF-D-02-P0` 是recent/pop/done成功language进入freeze前的blocker：

- 从captured head只读header沿Parent反向扫描，定位能覆盖latest 6 completed turns的保守suffix（包含至多一个
  open turn）；扫描穿过第7个Observation并以其`Parent`作为seeded `startExclusive`，或到达root；不在这里
  复制第二套turn reducer；
- header-first累计examined raw events与logical payload bytes；任何payload materialization前先把其declared length计入
  budget。并在同一work/payload budget内发现、验证execution与governing setup seed，setup payload也计入budget；
- 只调用带已验证seed的bounded planning-window forward fold作为唯一semantic reducer，再取latest 6。禁止传
  `planningSeed:null` 后由内部 `ResolveGoverningSetup` 向root做预算外查找，也禁止任何seed-discovery unbounded fallback；
- result显式区分Snapshot / LimitExceeded / UnsupportedSchema / Corruption；future schema不能误称corruption，
  limit不fallback到root-wide replay；
- pop在expected head上bounded定位intended ancestor与authoritative popped user text，并用production serializer
  预编码、检查exact `{poppedUserText}` response cap；全部成功后才CAS移动Ref，成功后直接返回预编码bytes；
- pop response不携带recent/readiness。browser收到200后、任何后续await前立即清空旧rewind token、保存draft并把
  recent标记loading/unavailable，再单独GET bounded recent。该GET的limit/busy/network failure只表示view不可用，
  不否定已成功的pop，也绝不能触发pop retry；POST outcome不明时同样只做current/recent reconciliation；
- browser在发送POST前从token-bound coherent recent保存latest `userText`作为provisional draft；200 receipt
  覆盖/确认它。若CAS成功但response丢失，reconciliation确认旧turn已移除后仍可恢复该draft；绝不为取回receipt
  自动重发mutation；
- post-pop recent GET只有exact-current projection才能返回200并签发新rewind token；current busy cache不得伪装
  exact success。除非未来出现“单响应必须含exact post-state view”的真实需求，拒绝为此新增
  exact-historical/prospective readiness seam；
- Galatea在projection成功后仍用实际production serializer检查final composed JSON UTF-8 bytes；
- recent/pop limit返回typed code；不能把stale cache伪装成最新success；
- SSE必须复用同一bounded recent，不复制第二套unbounded snapshot。

具体 `MaximumExaminedRawEvents`、cumulative logical bytes、final JSON bytes是
`Prototype-required-product-decision`。先锁fail-closed typed limit；没有真实需求前拒绝新增external pagination、
cursor、silent truncation或preview endpoint/API，它们会引入新的fidelity与mutation语义；这不禁止pop所需的
内部preflight projection与pre-encode。

## 5. CF-D-02b — SSE current language

### 5.1 Current state

current endpoint把 `StreamEventDto(string, object?)` 写成LF-framed `event/data`：

| Event | Current payload | Consumer status |
|:--|:--|:--|
| `meta` | phase；有时含changed/fallback/toolName/toolCallId | browser只消费turn/input/tool-loop几个phase |
| `reasoning-delta` | delta | concatenate display |
| `text-delta` | delta | concatenate display |
| `done` | recent + Completion errors | browser只消费recent |
| `error` | message，可选failureReason | browser只消费message |

subscription在同一lock内capture replay并register，因而没有replay/live gap；但replay list在单个
`GalateaLiveTurn`内单调且无bound增长，完成后还由`_lastTurn`持有到下一轮替换；每subscriber使用unbounded
channel。disconnect只结束subscription，不取消turn；显式stop是独立HTTP command，该边界正确。

现有缺口：

- `done/error` 在normal production各发一次，但类型系统允许double terminal、terminal后publish或无terminal
  `Complete()`；fatal/invariant exception可能形成browser持续EOF/reconnect；
- concurrent publishers在lock内建立replay order、lock外写channel，live与future replay顺序可能不同；
- unexpected exception message和provider/internal `failureReason`可能泄漏为wire；
- `done.errors`、toolCallId、reasoning/tool finish等没有first-party consumer；
- JS unknown payload静默忽略、malformed JSON重连、EOF-before-terminal重连；没有id/Last-Event-ID/heartbeat。

### 5.2 Locked V1 direction与未决payload

path随HTTP deployment hard-cut为 `/api/v1/chat/turns/{turnId}/events`。只保留五类typed internal events：

```text
status          { code, changed? }
reasoning-delta { delta }
text-delta      { delta }
done            { completed outcome；recent availability待P0决定 }
error           { code, message }
```

- status closed set：`generating|normalizing-input|input-normalization-finished|using-tools`；只有
  `input-normalization-finished` required `changed:boolean`，其他status不得带它；不冻结不可靠的fallback字段；
- delta必须nonempty；chunk segmentation不是contract，consumer只能concatenate；
- done event category保留，删除未消费的Completion errors；recent可用时必须精确复用HTTP V1 shared recent，
  但P0关闭前不冻结completed-but-recent-unavailable的payload；
- error只允许coarse code与sanitized message，删除failureReason/provider detail；exact public code ledger仍是
  `Prototype-required-decision`，至少必须区分operator stop、server shutdown、completion failure、recovery/config
  unavailable与internal failure，不能把它们都压成provider error。durable-completed后的P0 view failure属于
  done completed-outcome决策，不得回流成turn error；
- unknown event/property、wrong-case/type、null/missing、terminal后data均为protocol error；
- 对所有process-alive nonfatal turn，done/error恰好一个且必须最后；fatal process/transport failure仍可能EOF而没有
  terminal，browser绝不能把它当success；
- error后丢弃未提交partial display；只有携带可用shared recent的completed outcome才提交durable display，
  `recent-view-unavailable`（若被选择）必须触发独立refresh/error UX；
- publish sequencer必须让live/replay有同一线性顺序。

JS遇到protocol-invalid replay必须停止并显示version/protocol failure，不能对同一确定性错误无限重连；
EOF-before-terminal或404后查询current：同turn仍running才重连，terminal/recovery状态则刷新durable view并报告
interrupted，current查询失败只做有限重试。不能把“live turn不存在”直接推断为durable completion。

SSE framing直接收窄并锁为UTF-8、LF：每个event恰为一个 `event: <name>\n`、一个单行
`data: <json>\n` 和一个终止空行；不接受或生成 `id:`、`retry:`、comment、multi-`data:`或CRLF变体。
server exact-output fixtures负责证明payload JSON不含duplicate；browser不新增通用raw-token scanner，仍在parse后
执行event-local exact shape/type检查。

### 5.3 Retain / Prototype边界

- `Retain` V1语义：在明确的whole-turn bound内，重新订阅得到从头等价表示；无event id；disconnect不取消turn。
- `Adopt-direction / blocking`：实现bounded subscriber channel；channel full时只断开/移除slow subscriber，让
  browser查询current后从头重连。满channel不能可靠再写in-band error，因此不为subscriber overflow定义wire code。
- `Prototype-required-decision`：为whole-turn replay建立event count或serialized-byte cap，并为terminal预留budget；
  cap hit时必须另行锁定turn cancellation/abandon、terminal outcome与exactly-one transition。它不是subscriber
  overflow。继续使用unbounded replay list/channel不能进入freeze。
- `Adopt-direction / blocking`：browser在decode前累计per-connection received bytes并限制maximum raw-frame bytes；
  使用fatal UTF-8 `TextDecoder`，EOF执行final flush以拒绝残缺sequence。invalid UTF-8、缺delimiter导致的frame
  超限或connection累计超限都是protocol error；具体数字是`Prototype-required-decision`，并必须与server whole-turn
  encoded replay cap建立可执行relation。
- snapshot coalescing、Last-Event-ID、heartbeat、cursor/ack协议先`Reject first cut`；它们不是解决基本内存边界
  所必需的首个抽象。
- `done.recent` 的P0 projection/final-byte gate仍是整个SSE freeze blocker。turn已经durable完成后发现display limit，
  不能把它误报为provider失败，也不能返回stale recent。必须在P0数字锁定时选择：由上游bounds保证done总能构造，
  或定义typed `recent-view-unavailable` completed outcome；在此之前不冻结done exact payload。

## 6. Candidate ledger

| Candidate | Decision | Boundary |
|:--|:--|:--|
| D02 shared `/api/v1` direct cut | `Adopt` | server + cache-busted browser atomic；old route 404 |
| D02a strict endpoint DTO/options | `Adopt` | 包括duplicate rejection；accepted language只收窄，不自研parser |
| D02a minimal error/busy shapes | `Adopt` | machine code稳定，文本diagnostic |
| D02a success/current field cuts | `Adopt` | pop只留poppedUserText receipt；recent另取exact-current snapshot |
| D02 request numeric bytes | `Prototype-required-decision` | original/normalized/body/materialization都需同一policy |
| D02-P0 bounded recent locator/result | `Adopt-direction / blocking` | numeric work/payload/final-byte limits待选择 |
| pagination/truncation/chunk endpoint | `Reject first cut` | 真实limit hit前不增加fidelity/state-machine |
| D02b typed five-event categories | `Adopt` | done exact payload仍受P0阻塞；不与HTTP/CLI建generic envelope |
| D02b exact UTF-8/LF frame grammar | `Adopt` | 单event/data/空行；不接受id/retry/comment/multi-data/CRLF |
| D02b exact terminal + publish sequencer | `Adopt` | process-alive nonfatal turn；修复当前类型系统/ordering缺口 |
| bounded subscriber channel + overflow disconnect | `Adopt-direction / blocking` | numeric cap待选择；无in-band overflow code |
| whole-turn replay cap + terminal reserve | `Prototype-required-decision / blocking` | cap-hit turn/terminal transition待锁 |
| browser receive/raw-frame/fatal-UTF8 bounds | `Adopt-direction / blocking` | numeric cap与server replay relation待选择 |
| whole replay/no id/disconnect semantics | `Retain V1` | 以明确whole-turn bound为前提 |
| cursor/heartbeat/snapshot/ack | `Reject first cut` | 非基本memory-safety所需；按真实运行证据重开 |
| per-frame version / versioned event names | `Reject` | path已经完成negotiation |

## 7. R3 packages与gates

本轮不实施D02。下一轮若批准，按以下顺序避免把blocker藏在wire后面：

1. **D02-P0 bounded recent**：先做header-only反向suffix locator、同budget execution/setup seed discovery、
   seeded bounded forward fold、typed limit、pop最小receipt pre-encode-before-CAS与production encoder relation；主线程
   必须显式选择numeric budgets与durable-completed oversize语义。
2. **D02b-A0 stream bounds decision**：先锁subscriber channel cap/overflow disconnect、带terminal reserve的
   whole-turn replay cap与cap-hit turn transition，以及browser per-connection/raw-frame/fatal-UTF8 limits与server
   cap relation；消费P0已锁的completed-view outcome，不二次拥有该决策；不实现SSE wire。
3. **D02a HTTP core**：`/api/v1`、strict DTO/binding、自有error、success/current cuts、browser migration；旧route
   exact 404，不增加compatibility。
4. **D02b SSE server**：typed union、sanitized error、bounded channel/replay、exact terminal、linear publish、exact
   frame fixtures。
5. **D02b browser**：strict parser、protocol/EOF/reconnect状态机，并与server/cache token原子发布。
6. **Combined R4**：server+browser exact fixtures；provider/normalizer zero-call failure；raw-head/no-mutation；recent/pop/
   done shared composed-byte gates；pop response-loss以token-bound provisional draft恢复且不得造成第二次retraction，
   只有exact recent成功才能签发新token；
   SSE UTF-8/LF exact frame与forbidden-field fixtures；old route absence；serial Galatea/SessionJournal/solution tests。

实现中出现generic JSON AST、HTTP/SSE/CLI result hierarchy、dual version routes、silent fallback、先mutation后limit、
为了pop引入historical readiness authority，或为了未知需求引入pagination/cursor时，必须暂停并重新证明收益。

## 8. Remaining boundary

本plan lock不修改或冻结raw SessionJournal events、RecapGrid durable authority、Completion provider stream、root config
或HTML bootstrap。D02-P0的现有unbounded read是当前实现风险，但本轮只读；在它和numeric budget/oversize policy
关闭前，HTTP/SSE不能进入R5 stable/frozen声明。
