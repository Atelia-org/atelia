# SessionJournal Contract Freeze R2 — D02 candidate implementation and combined R4 evidence

状态：`CF-D-02-P0`、`CF-D-02a`、`CF-D-02b` candidate implementation与combined R4 complete；R5 Pending  
候选HEAD：`0f441f901569adcadafbcc81a8dc3fe1a253c60a`  
决策基线：`66dd87fc2f38ce657241a905f5b997cebe577355`  
记录日期：2026-08-17

## 1. Evidence边界

本文记录从bounded recent产品决策到HTTP/SSE V1 candidate的提交链、最终contract shape与分时验证证据。
它证明列出的candidate在各自commit时通过了相应gate；不把这些运行伪装成一次发生在文档HEAD上的全量
final run，也不宣布任何tier已经stable或frozen。

本文不改变或认证raw SessionJournal event wire、RecapGrid durable authority、Completion provider wire、
root config或HTML bootstrap。数值bounds仍是pre-release `Prototype locked` 产品选择；未来若改变，必须形成
新的candidate与R4 evidence。

## 2. Commit-pinned implementation chain

| Commit | Semantic unit | Result |
|:--|:--|:--|
| `66dd87fc` | D02-P0与D02b-A0 product lock | 锁定recent/pop与SSE数值、oversize/cap-hit语义；只改文档 |
| `b65f3ad6` | bounded recent + prepared pop CAS | 引入同operation budget、seeded bounded fold、closed recent result与pre-encode-before-CAS pop |
| `818387e4` | P0 independent-review tail | 关闭corruption classification、AsyncLocal scope isolation与public exception leakage；补mutation/public gates |
| `0cb93711` | HTTP V1 direct cut | server与cache-busted browser切到`/api/v1`、strict DTO/error/success shapes |
| `f9ebc37d` | HTTP review tail | 删除旧route与server-owned request-size metadata，关闭body/recent/pop/typed-error seams |
| `d1369fde` | HTTP startup race tail | recent busy后重读current，关闭browser初始化的idle-to-running TOCTOU |
| `57d05f4d` | SSE V1 candidate | typed frame union、bounded replay/channel、strict browser parser与terminal state machine |
| `201fec74` | SSE independent-review tail | 关闭terminal publication、UTF-8/frame/EOF、overflow、reconnect与mutation gates |
| `0f441f90` | P0 public API hygiene tail | 把12个closed result record收为plain class，删除120个无需求的record synthesis members；type/property pattern语义与HTTP/SSE wire不变 |

没有提交保留旧HTTP route、dual SSE grammar、compatibility reader或silent fallback。每个tail都收窄同一个
candidate，而不是建立并行V1/V2 truth。

## 3. Final candidate contract

### 3.1 D02-P0 bounded recent与pop

- recent只返回latest 6 completed turns；整个operation共享最多4,096次physical header preview visit与
  16 MiB cumulative decoded logical payload。
- locator从captured head反向证明保守suffix，在同一budget内发现并验证execution/governing setup seed，随后只
  调用seeded bounded forward fold；没有`planningSeed:null`或root-wide fallback，也没有第二套turn reducer。
- closed结果区分Snapshot、LimitExceeded、UnsupportedSchema与Corruption；I/O、cancellation与dispose仍按owner
  contract传播。
- Galatea用production serializer对recent最终JSON施加4 MiB encoded-byte cap；busy、limit、unsupported与
  corruption不伪装成stale success。
- pop先准备owner/branch/head-bound capability，限制popped display source为256 KiB UTF-8，并预编码exact
  `{poppedUserText}`且限制为2 MiB；proof、projection与encoding完成后才CAS。CAS后不再做turn display projection
  或serialization，响应写入同一份预编码bytes。
- browser在mutation可能变得indeterminate前保存token-bound provisional draft并失效旧token；receipt或transport
  丢失只做current/recent reconciliation，绝不自动重发pop。

这些上限均inclusive；`maximum + 1`必须在下一次header/payload materialization、CAS或response write前
fail closed。

### 3.2 D02a HTTP V1

- 完整API group直接cut到`/api/v1`；旧`/api/*`按原method返回404，没有redirect、alias或dual route。
- JSON body只接受`application/json`与可选UTF-8 charset，不接受`Content-Encoding`；exact camelCase，unknown、
  wrong-case、duplicate、missing required、wrong type、required null、comment与trailing comma均拒绝。
- request body上限1 MiB，由application-owned `Content-Length` check与最多读取`remaining + 1`的counting stream
  执行；没有同值的Kestrel/MVC endpoint metadata抢先产生非typed 413。
- original message与normalized message分别限制为64 KiB UTF-8；connection id沿用Completion owner的128-byte cap。
- success surface为start/resume 202 `{turnId}`、stop 204 empty、pop `{poppedUserText}`、五字段current与同head
  coherent recent。除busy的`{code,error,turnId}`外，failure只用`{code,error}`。
- auth早于maintenance，maintenance早于media/body decode；unknown route不被maintenance伪装成503。response
  started后不追加JSON，cancellation不被改写为protocol 500。

### 3.3 D02b SSE V1

SSE只生成五类closed events：

```text
status          { code, changed? }
reasoning-delta { delta }
text-delta      { delta }
done            { recent: RecentTurnsResponseV1 | null }
error           { code, message }
```

- frame是strict UTF-8与LF：exact一个`event:`行、一个单行`data:` JSON和终止空行；不生成或接受id、retry、
  comment、multi-data或CRLF grammar。
- process-alive nonfatal turn恰有一个最终done/error；terminal后不能publish。fatal process/transport failure可以
  EOF without terminal，browser必须按current状态有限reconnect或停止，不能把EOF当success。
- nonterminal preview最多4 MiB与16,383 events；terminal reserve为5 MiB与1 event；whole replay最多9 MiB与
  16,384 events。preview命中任一cap只进入internal `PreviewSuppressed`并丢弃后续preview，不停止provider、
  不改变durable outcome，也不新增wire status。
- 每subscriber channel容量为256个immutable frame references；full只断开该subscriber，不取消turn，也不尝试
  写入不可靠的in-band overflow error。
- browser在decode前限制每connection 9 MiB、每raw frame 5 MiB，使用fatal UTF-8 decoder并在EOF final flush；
  server/browser relation由composed-byte fixtures锁定。
- durable turn已完成但bounded recent当前不可得时，唯一terminal仍是`done {recent:null}`；browser再走HTTP recent
  获取typed view outcome，不把display failure改写成provider/turn failure。

## 4. Combined R4 gates与运行时点

下列结果来自不同package closure时点；它们共同覆盖candidate chain，但不是一次伪造的“单一HEAD全量重跑”：

| Candidate point | Recorded gate | Result |
|:--|:--|:--|
| `818387e4` P0 tail | `SessionJournal.Tests` full | 452 passed / 0 failed |
| `d1369fde` HTTP tail | HTTP browser Node contract suite | passed |
| `201fec74` SSE tail | `Galatea.Server.Tests` full | 139 passed / 0 failed |
| `201fec74` SSE tail | HTTP与SSE两个Node contract suites | both passed |
| `201fec74` combined source candidate | `Atelia.sln` build，serial `-m:1 -nr:false` | 0 warnings / 0 errors |
| `0f441f90` API hygiene tail | `SessionJournal.Tests` full | 453 passed / 0 failed |
| `0f441f90` final code candidate | `Galatea.Server.Tests` full | 139 passed / 0 failed |
| `0f441f90` final code candidate | `Atelia.sln` build，serial `-m:1 -nr:false` | 0 warnings / 0 errors |

Node suites在SSE final candidate `201fec74`通过；其后的`0f441f90`只改SessionJournal result的CLR class shape与
对应reflection test，没有修改Galatea production、HTTP/SSE DTO、JavaScript或wire grammar。最终.NET验证则在
`0f441f90`重跑。这里按实际时点列证据，不把它们伪装成一次相同HEAD下的run。

静态/变异gate还证明：

- 4,096与16 MiB的max/max+1、header-before-payload reserve、seeded no-fallback、future schema/corruption分类；
- old method/route矩阵为404且provider、normalizer、session open与raw head mutation均为零；
- no-body、media、encoding、duplicate/shape mutation与application-owned413；
- recent busy不返回stale 200，browser在busy race中重读current；
- pop response-loss保留draft、只有一个mutation POST，CAS后source/compiled body没有projection/serialization；
- exact frame bytes、forbidden field/grammar、terminal reserve、subscriber overflow、PreviewSuppressed、fatal UTF-8、
  frame/connection max+1、EOF-before-terminal与bounded reconnect状态。

### 4.1 Final S public inventory

沿用R0工具与effective-public/declared-member口径，在`0f441f90`对`Atelia.SessionJournal`生成两遍且
byte-identical：

| Point | Types | API rows | API SHA-256 |
|:--|--:|--:|:--|
| R0 `380df30f` | 148 | 1,339 | `64363409b9af31c04648ee6d464b3527029acc0e272c09502f1e4c8df0910e03` |
| D02 final `0f441f90` | 162 | 1,358 | `f1a24eac3142c6ddc8e97418127d8ad4b908866c35b9730d8c9df10db6d42018` |

净变化是`+14 types / +19 API rows`。`201fec74`时新增的12个closed result records曾连带生成120条clone、
equality、hash与print machinery；`0f441f90`只把它们改成plain classes，保留closed result types、public getters、
internal construction与pattern matching，恰好删除这120条偶然surface。新增type中的limit enum与prepared rewind
capability同样保留；本tail没有删除P0表达能力，也没有改变HTTP/SSE serialization。

construction inventory为379行，SHA-256
`8cbf9ba4f803260bb1acc117b9fbe00f1613655c53cd9ab30176838606753d59`；两遍byte-identical，并与R0的
`84 visible ctors / 217 init / 0 set / 78 record clones`及hash完全一致。换言之，D02增加的是必要的可读结果、
limit分类与owner-issued capability，不把新的public construction/copy authority一并冻结。

## 5. 复杂性复核

R3/R4没有出现需要推翻设计的复杂度信号：

- 没有pagination、cursor、Last-Event-ID、ack、heartbeat或snapshot coalescing；当前真实需求只要求bounded
  whole-turn replay与from-start reconnect。
- 没有旧route、dual grammar、per-frame version或compatibility hierarchy；path `/api/v1`已承担negotiation。
- 没有generic HTTP/SSE/CLI result hierarchy、JSON AST或schema framework；HTTP使用endpoint-local DTO/validator，
  SSE使用一个局部bounded encoder与一个browser pure parser/state machine。
- bounded encoder/parser是执行encoded-byte、fatal UTF-8、terminal与accepted-language contract所必需的局部机制，
  没有成为新的public authority或跨owner abstraction。
- recent仍复用SessionJournal唯一semantic reducer，SSE done仍复用HTTP recent DTO；没有第二个turn reducer、
  historical readiness authority或并行display taxonomy。
- P0-local closed results保留domain-specific状态代数，但改为plain classes，避免为只读结果意外承诺record
  equality/clone/print contract；没有借此重开跨owner generic result hierarchy。

因此没有理由为了“更通用”继续扩张。只有真实limit-hit、需要增量resume或跨deployment compatibility的证据出现时，
才重新评估pagination/cursor/versioning。

## 6. Remaining boundary

`CF-D-02-P0`、`CF-D-02a`、`CF-D-02b`现为commit-pinned、combined-R4-complete candidate。
这允许后续转向`CF-D-03`、targeted `CF-B`与`CF-C` evidence，但不自动完成R5。最终是否把哪个tier声明为
stable/frozen，仍需单独批准support-role map、compatibility policy、candidate tag与必要的部署/真实环境证据。
