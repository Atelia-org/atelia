# SessionJournal Contract Freeze R2 — CF-C-02 implementation evidence

状态：candidate implementation、independent review与package-local R4 complete；R5 Pending  
source candidate：`3599c510188656b282722baddaee974b75a4ffb9`  
记录日期：2026-08-17

## 1. Evidence boundary

本文记录HistoryTimeline、RecapGrid Store与Rewriter V3三个companion-contract证据包，以及从用户指定
legacy export执行的一次current-candidate disposable rebuild。目标是把可重建性、独立golden与operator
classification变成可执行证据，不把“旧数据可以丢弃”误解为删除corruption、query或CAS proof的理由。

实际Galatea repo
`prototypes/Galatea/.atelia/galatea/sessions/cyber-session-journal-recap-grid`未被修改；final source-candidate
rebuild输出位于`/tmp/atelia-cfc02-source.jkbQNb/`。本轮没有启动Galatea Host、没有构造Completion provider、没有调用模型，
也没有把对话、prompt或recap正文写入tracked evidence。

## 2. Commit map与裁决

| Commit | Package | Decision / contract delta |
|:--|:--|:--|
| `8605a62194d638544c3c702885344d3fa3645a0b` | CF-C-02 Rewriter | test-only：work-tail完整literal；runtime/output/input/prior/history五协议轴均在route/dispatch前拒绝 |
| `6e4955cacf45fc8de40ade5099c0b8f574c99e5c` | CF-C-02 HistoryTimeline | test-only：locator/head literal、SQLite V2 independent fingerprint、metadata/head/count分层 |
| `b4559d7c7b7e186cbfea1eec9a3e38d3c73efc77` | CF-C-02 Store | 完整metadata identity进入所有open/operator共同gate；坏instance id不再泄漏`ArgumentException` |
| `fa6a05954cee948a718c964b86b0e96fcfdb7524` | Store classification tail | `user_version`先于app id、metadata与catalog分类；future shape保持typed Unsupported |
| `3599c510188656b282722baddaee974b75a4ffb9` | `recap-grid init` readiness | existing Store在原exclusive lease内执行read-only open-level identity验证；invalid不再误报ready |

没有提交Schema V3、migration、dual reader、generic schema framework、自动repair/reset或新的public result。
valid V2 wire与CLI envelope不变；malformed/future Store只从错误的成功或模糊异常收窄为既有typed failure。

## 3. HistoryTimeline independent evidence

`6e4955ca`增加三层外部oracle：

- locator、empty head与selected head的完整canonical JSON literal，覆盖nullable与non-null address分支；
- 从factory-created SQLite查询`application_id`、`user_version`与ordered `sqlite_schema`，使用test-owned
  length-prefixed transcript计算固定SHA-256
  `7fbd3b1ee14ecfa50cb5194ac5d9b3cda3e55edaed5131f6d72ad7fada321b11`；
- application ID、metadata schema/timeline/ref、head digest与合法重算但异scope head的mutation均阻止normal open；
  非负stale counts仍允许O(1) normal read，但full Verify必须因物理计数不符失败。

fingerprint不引用production `SchemaSql`、`ExpectedSchemaEntries`、`ApplicationId`或`SchemaVersion`，并显式锁住
6 tables + 6 triggers。由此确认以下字段为intentional proof，而非待删双重权威：locator ref/generation、metadata
timeline/ref/schema、head digest、counts、selected path与Merkle commitment。

## 4. Store validation与classification

Store V2仍使用四层identity：`application_id`、`user_version`、单一metadata identity与exact `sqlite_schema`。
本轮发现并关闭三个实际缺口：

1. Export此前不读取完整metadata identity，可能接受坏`schema_version`或instance id；
2. malformed on-disk instance id可能向operator泄漏未映射`ArgumentException`；
3. future `user_version`若同时删除`store_metadata`，旧复合query会在分类version前先报missing table。

最终顺序是：先独立读取`user_version`；确认V2后才读取app id、metadata count、五个schema objects与完整
metadata identity。test-owned fingerprint为
`3b14f5e58f4012f699b9314b96f145dc43e878fbbc7e8d25574991319281343c`，固定app/user version、五个
catalog rows、instance-id shape与initial counts，不复用production `SchemaV2.sql`或expected-schema builder。

mutation matrix覆盖future version、wrong app id、metadata absent/duplicate/wrong singleton/schema/bad id、unexpected
与missing schema object，并跨Create/Open/OpenReader/Inspect/Export/Verify验证现有result分类。future version优先于
后续shape损坏；其他V2 corruption为Invalid/Unhealthy。

## 5. `recap-grid init` existing Store readiness

四owner审计表明，Cadence existing会decode，Timeline existing虽只验locator但随后Control会OpenReader，Control
existing也会decode/scope；只有最后一步Store此前只凭regular file existence返回`AlreadyExists`，因此可能令整个
`init`返回`ready`/exit 0。

`3599c510`在Store Create已经持有的exclusive lease内构造read-only store并调用`ReadIdentity`：它执行bounded
open-level schema/metadata/count sanity，不跑随数据量增长的full Verify，不释放/重取lease，也不产生自动迁移或
repair。valid existing仍是`AlreadyExists`；future/invalid existing使用现有`Invalid` result，使CLI返回
`store-failed`/exit 2。

tests同时锁定：Store DB bytes不变、CLI provider factory调用为0，以及raw、Timeline、Cadence、Control与Grid
domain snapshot在failed repeat-init前后完全一致。`init`仍是四owner顺序、非事务性command；本包没有尝试构造
跨owner transaction。

## 6. Rewriter V3 evidence

work-tail现以完整literal锁定：schema、logical column、topic、user prompt template与target carrier/block key全部
固定，不能只凭局部`Contains`通过。五轴mutation分别改变：

- runtime protocol；
- output protocol；
- input rendering protocol；
- prior projection schema；
- history segment rendering schema。

每一项均返回`ProtocolUnavailable`，并断言route resolver与invoker调用都为0；后置work runtime mismatch仍证明
先前work已准备时整批也不会dispatch。五个ID具有不同演进轴，继续Retain；合并它们会强迫无关协议同步升级，
另加suite ID则只会制造新的冗余authority。

## 7. Disposable legacy rebuild

current source candidate通过
`prototypes/SessionJournal.Cli/bin/Debug/net10.0/Atelia.SessionJournal.Cli.dll`执行；该CLI DLL的SHA-256为
`81da3c38835ced60bc0d1fda53a1fdb3116ba082ef8f5f6802cdaa6e91a125c2`。输入为：

```text
prototypes/Galatea/.atelia/galatea/sessions/cyber-copy-upgraded/chat-session-legacy-upgrade-export.json
```

执行两次不带`--force`的fresh import。source schema为
`atelia.chat-session.legacy-upgrade-export.v1`，大小1,281,881 bytes，SHA-256为
`b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`。两次报告在去除output path后exact一致：

- 1 SessionCreated；
- 1 runtime setup、4 prompt setups；
- 71 Observations、71 imported Actions；
- 明确跳过2 compactions与2 legacy recaps；
- warnings 0，final head `ej1:00000487000004330000000100000000`。

fresh repo的`legacy-root inspect`为0 entries，Store/Timeline为Absent，且这些只读命令创建0个derived/control文件。
随后在另一个fresh repo provider-free执行scaffold、四域`init`、Timeline sync、Control asset/recipe登记；最终：

| Gate | Result |
|:--|:--|
| `timeline inspect` / `verify` | available / available |
| `cadence inspect` | available |
| `control inspect` / `verify` | available / available |
| Store `inspect` / `verify` | available / healthy |

derived-only步骤前后的offline raw validation在去除repository path后byte-exact，repeat import的逻辑validation也exact。
两次fresh import的physical RBF bytes不相同，因此本文只承诺同一Ref/address/event/history语义，不错误宣称物理文件
deterministic。最终candidate再执行一次valid repeat-init，四owner均为`already-exists`、outer status为`ready`，
整个repository file set与bytes完全不变；run root中provider/call-log artifacts为0。

这条gate证明raw可从固定export重建、sidecar可重新provision并通过owner验证；它不冒充非空Cells的provider rebuild
或内容质量证据。实际recap content仍需要显式bounded provider build与独立人工审阅。

## 8. 分时验证与independent review

| Candidate | Gate | Result |
|:--|:--|:--|
| `8605a621` | Runtime focused / full / solution build | 7 / 7；55 / 55；0 warnings / 0 errors |
| `6e4955ca` | new History cases / existing strict cases / History full / PublicSurface | 10 / 10；10 / 10；182 / 182；6 / 6 |
| `b4559d7c` | Store focused / full / CLI provider-zero / solution build | 10 / 10；53 / 53；1 / 1；0 warnings / 0 errors |
| `fa6a0595` | precedence focused / Store full / solution build | 10 / 10；53 / 53；0 warnings / 0 errors |
| `3599c510` | Store focused/full；CLI focused/full；solution build | 9 / 9、53 / 53；1 / 1、112 / 112；0 warnings / 0 errors |

Rewriter、History、Store metadata/tail及Store version precedence均经过非作者只读review，findings已关闭。
各结果来自package closure时点；本文不把它们描述成对文档commit的一次全量test rerun。

## 9. Route adjustment与停止线

- CF-C-02 complete；没有 durable field deletion或Schema V3候选。
- 下一优先candidate是standalone `recap-grid timeline create` existing readiness：它没有`init`中的后续Control gate，
  应单独调查OpenReader-level validation，不能与Store修复或四owner transaction捆绑。
- History `SchemaSql` / `ExpectedSchemaEntries`重复是真实内部化简机会；独立fingerprint已提供安全网，但只在单一
  schema-entry列表驱动create+verify能够明显净减行时实施。禁止SQL parser、generic SQLite framework或hidden
  in-memory oracle。
- 完成上述小型operator/readiness裁决后整理support-role map、wire inventory、upgrade policy与candidate commit map，
  进入R5 preparation。
- R5仍Pending；本文不批准stable/frozen tier或tag。
