# SessionJournal Contract Freeze R2 — R5 candidate evidence

状态：R5 candidate **ready for approval**；final gates complete；逐Tier approval与tag Pending；**未声明stable/frozen，未创建tag**  
source candidate：`a77ed16c1ddef949dc519811fde56600db38316e`  
记录日期：2026-08-17

## 1. Evidence boundary

本文为[active plan](../work/active/session-journal-contract-freeze-r2.md)的R5 preparation记录：固定current
support-role inventory、统一wire/upgrade policy、candidate commit map，并为最终同一candidate gate保留明确状态。
规范性candidate shape见
[current Contract R2](../current/contracts/session-journal-contract-r2.md)。

`a77ed16c`是当前product/test source candidate；后续只含本文、current contract与router/plan更新的docs commit，
必须以path-scoped diff证明没有product/test drift，不能把docs HEAD冒充未经验证的新source candidate。

本轮inventory只从`git archive a77ed16c`读取tracked source，未读取ignored Galatea config、secret、actual repository
或conversation content；未启动Host、未构造provider、未调用模型。历史evidence只认证各自exact candidate；本文在
final gate填入前不把分时结果描述成current-HEAD rerun。

## 2. Candidate commit map

### 2.1 Plan、inventory与priority cuts

| Commit | Kind | Semantic unit |
|:--|:--|:--|
| `380df30fc069d2dfbc3c71fe1923e0442389ecd8` | docs/tool | R0 plan与reproducible inventory baseline |
| `677e94c9d931cfabc8137a24bf5163b3b494331f` | docs | R0 public/raw/companion/operational inventory |
| `5ca08be9abade505a89525e50fe9c215428f801c` | docs | R1 priority review与plan lock |
| `f37c1d77d0883a2735054cfd23adb2f607cdb282` | product/tests | CF-D-04 unified RecapGrid CLI envelope |
| `b77a4c67c3a683519f5a6c0dd50b3424e1a28cb3` | tests | encoded report/page relation tail |
| `46c44cd0c08af609383cdb6589e319f50df34d8c` | product/tests | Getter evidence construction cut |
| `3e530f954213c2d6e90e6c9b7a422cb152cf4401` | tests | Getter nonfriend/public surface tail |
| `e011a8b955e04e8f651273e0399a047a4db91050` | product/tests | Manager progress construction cut |
| `dba729749fbe6e2013d634386ad7f0d7b7b305a3` | product/tests | Online evidence construction cut |
| `58d8ae0656959570b5a48b4d4527c621759fc03b` | product/tests | Completion-owned strict connections V1 |
| `87079eaa14681e83b7d2db584b3b5bf59dd99ab5` | tests | connections migration equivalence/depth tail |
| `e1d785f0e29942ce698dcefe0392c5c535432b4d` | docs/semantic | priority cuts implementation evidence与active-plan closure |

### 2.2 D02 bounded recent、HTTP与SSE

| Commit | Kind | Semantic unit |
|:--|:--|:--|
| `fd863fea0b13f5751172999b95313e7d9da27b62` | docs/semantic | HTTP/SSE accepted-language、consumer与P0/A0 plan lock |
| `66dd87fc2f38ce657241a905f5b997cebe577355` | docs | P0/A0 numeric与cap-hit product lock |
| `b65f3ad66cc0dd668081e8ffc3d4d7094004ca0d` | product/tests | bounded recent与prepared pop-before-CAS |
| `818387e4e27c280d03c2c79ed835298da0efc644` | product/tests | P0 corruption/scope/public tail |
| `0cb93711dcc96094558407ad237093c601d42de7` | product/tests/browser | HTTP `/api/v1` direct cut |
| `f9ebc37d5f2b6ede372007817148f81625522459` | product/tests/browser | body/recent/pop/error review tails |
| `d1369fdea984912c2260718e517a396467c0a0ab` | product/tests/browser | recent-busy startup race tail |
| `57d05f4d6c193cf0c87466f8a4d7fc580dc29e2a` | product/tests/browser | typed bounded SSE V1 candidate |
| `201fec7498a87bf2a51426bbf7f9c71429c0977d` | product/tests/browser | terminal/UTF-8/overflow/reconnect tails |
| `0f441f901569adcadafbcc81a8dc3fe1a253c60a` | product/tests | P0 closed result record synthesis removal |
| `3167942ce088bcc281637584360fd12f22730ab3` | docs/semantic | D02 combined R4 evidence与candidate-facing Galatea contract |
| `c61b978e29eee02340b55acdcab6d1ea0317259a` | docs/semantic | D02 deployment、reconnect与validation boundary clarification |

### 2.3 Post-D02 support、config与companion evidence

| Commit | Kind | Semantic unit |
|:--|:--|:--|
| `fd66720e686ce3da2f96ca98f195a855d1c5a8e4` | product/tests | Galatea file DTO internalization |
| `f1a8da0b8d87bd6c417c857535df8799229aafd4` | product/tests | History owner-local descriptor assembly cut |
| `9f5de810c557659f339407c68402f1d3ef655b0d` | product/tests | Hosting snapshot-only telemetry |
| `233922635ff318b0d95557628023791c16701bd8` | product/tests | Galatea root config exact V1 hard cut |
| `8a2186f8d5e289aafffd7f79c30be2e8316210ea` | product/tests | Control future-schema classification与empty golden |
| `8f72cb663a234a1a9776e47a862d3437e04d53e4` | product/tests | root config no-BOM production bootstrap tail |
| `5f597c7ef38796fae780e3b05cbf7699791ade7d` | docs/semantic | D03、targeted CF-B与CF-C-01 closure evidence |
| `8605a62194d638544c3c702885344d3fa3645a0b` | tests | Rewriter五轴independent literal evidence |
| `6e4955cacf45fc8de40ade5099c0b8f574c99e5c` | tests | History locator/head/SQLite independent evidence |
| `b4559d7c7b7e186cbfea1eec9a3e38d3c73efc77` | product/tests | Store full metadata identity validation |
| `fa6a05954cee948a718c964b86b0e96fcfdb7524` | product/tests | Store future-version classification precedence |
| `3599c510188656b282722baddaee974b75a4ffb9` | product/tests | repeat-init existing Store readiness |
| `79413c78a57783efff858c4609baa19bfb861323` | docs/semantic | CF-C-02 implementation/readiness evidence closure |
| `c00df3d8cd8f25d9c97814d6aedceb7aeb242f07` | product/tests | RT-01 existing Timeline create readiness under same lease |
| `a77ed16c1ddef949dc519811fde56600db38316e` | product | SC-01 single History SQLite DDL source；final source candidate |

没有compatibility wrapper、dual reader/writer、generic parser framework、cross-owner result hierarchy、Schema V3或raw
event wire commit。初版R5 docs candidate为`29cc5561`；本review-tail docs commit只关闭文档finding。tag仍Pending，
且只能在final gate与用户tier approval之后创建。

## 3. Current public inventory

### 3.1 Method与tool identity

baseline开始和结束时HEAD均为`a77ed16c1ddef949dc519811fde56600db38316e`且worktree clean。SDK为
`.NET 10.0.110`；platform为`Linux 6.18.33.2-microsoft-standard-WSL2 x86_64`。

inventory在`/tmp/atelia-r5-inventory.QxEIvQ/`中执行：

- `product-src/`：exact `git archive a77ed16c`；
- `artifacts/tool/`与`artifacts/{S,T,O,C,G,H}/`：隔离restore/build outputs；
- `results/run1/`、`results/run2/`：每assembly的API/construction JSONL。

首次`--no-restore`因isolated artifacts没有`project.assets.json`得到预期`NETSDK1004`；随后restore/build仍完全位于
上述`/tmp` artifacts。tool与六个product均0 warnings / 0 errors。tool identity与R0相同：

- `Program.cs` SHA-256：`8a4f233dc5a4a2c5a8cd94a5611907b653f7583a54cd2ee6dcf53a967e44b6f7`；
- `.csproj` SHA-256：`4659c618b6dba7701e58431487c416c01bb65d466be3c2b49e5a14bb855aafa4`。

每次tool调用内部生成两遍并要求byte-identical；每个assembly又独立调用两次，run1/run2的API与construction
输出均经`cmp -s`确认byte-identical。

### 3.2 Exact results

| Alias | Types | Members | API SHA-256 | Construction lines | Construction SHA-256 |
|:--:|--:|--:|:--|--:|:--|
| S | 162 | 1,358 | `f1a24eac3142c6ddc8e97418127d8ad4b908866c35b9730d8c9df10db6d42018` | 379 | `8cbf9ba4f803260bb1acc117b9fbe00f1613655c53cd9ab30176838606753d59` |
| T | 227 | 2,592 | `8f257a497b890555d9c71c50c7eee19a285001f6c2e2e6d324e43bf6d58ca320` | 568 | `8b07fc60fc1ae7c28c4580ca2f3f491846454c3b2e24d6a402f49eb52d9df6f3` |
| O | 1 | 4 | `35b5c4c62b37807b8d8211bcbe40177a6d97f76559a5f677ad02edb67e57465a` | 1 | `c80c0478817fc961faf7c994c7e6a0ec6c1f0cc7de853bfd0e37de2877580d52` |
| C | 76 | 827 | `f0ab6567b5c8f3e4013107e93311ed94c4683151530c9e5b85f019b5ea7f274a` | 182 | `f886260ad094bde84848c634fe710917766cd0aedadbce486e35edc8026119a3` |
| G | 415 | 4,417 | `efde4a41f2c0f6cc8d77a441083d8a04d9fcb20f830be164b2b5fe15b6625452` | 941 | `5b0b146c432deb88bed2b4889314a16419b82a156da3f4e3e46b793392c96c84` |
| H | 20 | 221 | `53e3d95687bb9e0f856017c2673ad790a909ac4546b3894018a7f3ab127aa907` | 52 | `bfd51fb2eb4ca10c0f12bdee73fa2063b3bd3a011ec4d2086edd6ed3dce21f9a` |
| **总计** | **901** | **9,419** | per-assembly | **2,123** | per-assembly |

相对R0 totals `891 / 9,436 / 2,171`，current delta为`+10 types / -17 members / -48 construction lines`。
O/C的API与construction hash、S/H construction hash保持R0 byte-identical；S/T/G/H API及T/G construction按已批准
candidate变化。结果不是“所有901个type均stable”的声明；support classification由
[current contract §2](../current/contracts/session-journal-contract-r2.md#2-tier-d-support-role-map)拥有。

## 4. Wire、compatibility与non-promise closure

[current contract §§3–6](../current/contracts/session-journal-contract-r2.md#3-tier-a-rawrecovery-wire-inventory)
现已统一记录：

- Tier A raw event ID/body version、Prepared/recovery strict language与未来raw-preserving migration义务；
- Tier B History/Cadence/Control/Store/Rewriter exact slot/version/bounds/proof与explicit reprovision policy；
- Tier C route/connections/profile/root config、CLI、HTTP/SSE version、bounds、client/deployment边界；
- Tier D supported role与exported-but-not-promised boundary；
- no dual reader、silent migration、compatibility wrapper、generic parser/result hierarchy或provider/content guarantee。

RT-01 `c00df3d8`关闭standalone `timeline create` false-ready：existing locator在同一exclusive lease内以read-only
ledger验证schema/head与active policy，future/invalid返回既有`Invalid`、busy保持`Busy`，不跑full Verify、不迁移/repair。
SC-01 `a77ed16c`使一份12-entry owner-local DDL列表驱动tables → seed → triggers与verification Ordinal view；
independent test fingerprint未改，accepted Schema V2不变。

## 5. Final gate matrix

下表只记录本candidate的同一source gate。`Pending`不得用早期package-local结果替换；`NotRun`是刻意边界，
不是Passed的别名。

| Gate | Status | Exact result / boundary |
|:--|:--|:--|
| Current S/T/O/C/G/H isolated inventory | Passed | §3；两层byte-stability，tool与六product build 0W/0E |
| RT-01 focused/full/CLI/source review | Passed | `c00df3d8`：focused 9/9；History 182/182；PublicSurface 6/6；CLI focused 1/1、full 113/113；solution build 0W/0E；independent review PASS |
| SC-01 History full/fingerprint/source review | Passed | `a77ed16c`：focused 8/8；History 182/182；PublicSurface 6/6；solution build 0W/0E；independent review PASS；net -53 lines，test-owned fingerprint unchanged |
| Serial full solution tests | Passed | `dotnet test Atelia.sln --no-restore -m:1 -nr:false`：37 projects，4,629 passed / 0 failed / 0 skipped |
| Serial full solution build | Passed | `dotnet build Atelia.sln --no-restore -m:1 -nr:false`：0 warnings / 0 errors；23.40 s |
| PublicSurface/nonfriend/owner suites | Passed | solution run实际包含全部11个PublicSurface projects；WalkingSkeleton 27/27、Galatea.RecapGrid 7/7；construction/support negative gates green |
| HTTP/SSE server与Node client contracts | Passed | `galatea-http-v1.test.mjs` 1/1；`galatea-sse-v1.test.mjs` 1/1；同一source candidate |
| Provider-free disposable legacy rebuild/repeat-init | Passed | §5.1；`/tmp/atelia-r5-rebuild.fdXFt0/`，two fresh imports、offline/raw invariants、四owner gates与13-file repeat snapshots green |
| Scoped docs checker与source-vs-docs diff | Passed | checker 18 files / 0 diagnostics；`git diff a77ed16c -- ':!docs/**'`为空；all-tracked report-only为86 files / 11 diagnostics，全部是既有`archive/` missing targets，本包不修改archive |
| Independent docs review | Passed | initial review分别报告2项P1加precision findings、以及6项findings；`3e9a4b8d`逐项关闭后，两位reviewer tail re-review均PASS |
| Current ignored operator config | **NotRun** | 本轮禁止读取；历史content-free cutover不续期为current deployment证据 |
| Real provider / content quality canary | **NotRun** | 不属于contract freeze gate；provider calls必须为0 |
| Tier approval与tag | **Pending** | 由用户逐tier批准；本文不创建或建议既成tag |

### 5.1 Provider-free disposable rebuild

final rebuild固定source candidate `a77ed16c1ddef949dc519811fde56600db38316e`，run root为
`/tmp/atelia-r5-rebuild.fdXFt0/`，machine summary为`reports/r5-summary.json`。current candidate CLI DLL SHA-256是
`018c2dd23c4fa9716bc35984753aa9b4b2c85d99938d6927aafb1e3a7c87c1ae`。输入legacy export identity为
1,281,881 bytes、SHA-256
`b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`。本gate只读这份固定legacy export；
actual target repository与operator config均未访问。

两次fresh import的path-normalized reports exact相等；offline validation均为
`atelia.session-journal.offline-validation.v2`且normalized exact相等。final logical facts为148 events、
474,498 logical payload bytes、71 Observations、71 Actions、142 history contributions、Idle、Prepared 0、
Ref `000000000400001f`、head `ej1:00000487000004330000000100000000`。这里承诺logical equality，不宣称
physical RBF bytes deterministic。

derived workflow依次得到：

| Command | Status / fact |
|:--|:--|
| `scaffold` | `created`；1 Family、2 Definitions、1 Route |
| `init` | `ready` |
| `timeline sync` | `synchronized`；committed 1 Timeline row |
| `control provision-asset` | `applied` |
| `control compose-full-recipe` | `created` |
| `control put-recipe` | `stored` |
| Timeline inspect / verify | `available` / `available` |
| Cadence inspect | `available` |
| Control inspect / verify | `available` / `available` |
| Store inspect / verify | `available` / `healthy` |

raw inventory为3 files，derived workflow前后SHA snapshot exact不变。valid repeat-init为`ready`且四owner均
`already-exists`；standalone repeat `timeline create`同样为`ready`。两项操作各自比较同一13-file repository
snapshot，before/after byte-exact且没有新增/删除文件，从black-box路径覆盖RT-01的valid existing行为。

21个captured stderr files合计0 bytes。所有CLI命令在`bwrap --unshare-net`网络namespace中执行，run root中
provider/call-log artifacts为0。black-box CLI没有injectable provider-factory counter，因此本gate不把“没有网络且
artifact为0”夸大成可观测的factory call count；它只证明这些命令在禁网条件下完成且未留下provider artifact。

首轮harness把offline validation schema预设成v1，但current CLI exact schema是
`atelia.session-journal.offline-validation.v2`；import report的
`atelia.session-journal.legacy-import-report.v1`预设本来正确。两条fresh import及紧随其后的两条offline validation
共四条CLI进程均已成功；校准只修正harness的offline-validation assertion，复用已写入disposable run root的两份
import结果，没有重跑import。这是test harness assertion校准，不是product failure、retry或compatibility fallback。

## 6. Approval boundary

current source code与provider-free disposable rebuild gates已在`a77ed16c`收口；scoped docs checker为18 files / 0
diagnostics，且docs-only candidate相对`a77ed16c`没有product/test diff。两位reviewer的initial findings由
`3e9a4b8d`关闭，tail re-review均PASS，因此本candidate达到`ready for approval`。剩余`Pending`只有用户逐Tier
approval与tag；必须由用户明确选择Tier A/B/C/D中的哪些声明为stable/frozen并批准tag名称，未批准tier继续保持
candidate/Prototype，不被相邻tier带上。
