# SessionJournal Contract Freeze R2 — D03、targeted CF-B 与 CF-C-01 implementation evidence

状态：candidate implementation与package-local R4 complete；R5 Pending  
source candidate：`8f72cb663a234a1a9776e47a862d3437e04d53e4`  
记录日期：2026-08-17

## 1. Evidence boundary

本文记录D02之后三个窄工作方向的commit-pinned结果：Galatea root config V1 hard cut、三项高置信
public support-role cut，以及Control future-schema classification与independent empty-state golden。它只证明下列
exact commits和分时gate，不把这些结果解释为一次发生在文档HEAD上的全量测试，也不宣布任何tier
stable/frozen。

实际Galatea operator manifest位于Git忽略目录。本文只保存停服preflight、文件元数据、content-free hash与
provider-free load计数；不记录password、connection、endpoint、model、prompt、path value或其他operator值。

## 2. Commit map与裁决

| Commit | Package | Decision / contract delta |
|:--|:--|:--|
| `fd66720e686ce3da2f96ca98f195a855d1c5a8e4` | targeted CF-B / Galatea file DTO | `GalateaUsersFileConfig`与`GalateaRecapGridFileConfig`收为`internal`；public runtime config与HTTP DTO保持公开 |
| `f1a8da0b8d87bd6c417c857535df8799229aafd4` | targeted CF-B / HistoryTimeline | owner-local `BoundHistorySegmentRange` proof与`HistorySegmentDescriptorFactory`收为`internal`；descriptor与partitioner readable/input roles保持公开 |
| `9f5de810c557659f339407c68402f1d3ef655b0d` | targeted CF-B / Hosting | host-owned mutable telemetry collector改为public snapshot-only read；standalone injectable collector保持公开 |
| `233922635ff318b0d95557628023791c16701bd8` | CF-D-03 | Galatea root config direct cut到exact integer `v:1`；无versionless/dual reader或自动迁移 |
| `8f72cb663a234a1a9776e47a862d3437e04d53e4` | CF-D-03 tail | production bootstrap改为no-BOM UTF-8 bytes writer，并以真实bootstrap → strict reader → loader gate闭环 |
| `8a2186f8d5e289aafffd7f79c30be2e8316210ea` | CF-C-01 | 完整strict non-V2 Control discriminator获得typed Unsupported分类；新增independent empty whole-state literal golden |

这些提交是独立semantic units。CF-B没有与wire cut捆绑；CF-D-03没有把users、routes、secrets或runtime policy
并入connections superset；CF-C-01没有改变Control V2 canonical writer或增加generic schema framework。

## 3. Targeted CF-B support-role cut

### 3.1 Galatea file DTO

`GalateaUsersFileConfig`与`GalateaRecapGridFileConfig`只由Galatea assembly内的strict loader、bootstrap template与
friend tests使用。它们是file binding mechanics，不是consumer runtime input，也不是HTTP response contract。
`fd66720e`隐藏这两个DTO，并以reflection/export-absence gate保留以下合法public roles：

- merged runtime `GalateaConfig`与`GalateaUserConfig`；
- first-party HTTP DTO；
- existing loader/bootstrap owner，不新增public file-config construction path。

### 3.2 HistoryTimeline owner-local assembly

`BoundHistorySegmentRange`的构造函数原本已是`internal`，生产消费者全部位于HistoryTimeline owner assembly；
需要该proof的public factory对普通consumer并不可用。`f1a8da0b`把proof与factory一起隐藏，同时保留
`HistorySegmentDescriptor`、canonical codec与`HistoryPartitioner`的合法read/input role。

该commit后的T metadata inventory为 **227 exported types / 2,592 public member rows**；R0为229 / 2,609。
PublicSurface gate既断言两个owner-local类型不再export，也正向锁住descriptor与partitioner仍可见。没有
descriptor bytes或Timeline durable wire变化。

### 3.3 Hosting telemetry snapshot

`RecapGridRuntimeHost.Telemetry`与`RecapGridCompletionHost.Telemetry`曾把host-owned collector本体暴露给consumer，
允许外部调用`Record`。`9f5de810`用`ReadTelemetrySnapshot()`替代两个public collector property，并隐藏仅描述
lazy allocation的`IsMaterialized`。`BoundedRecapCompletionTelemetry`仍是合法public support role，因为
external composition可把它作为`IRecapCompletionTelemetry`注入runtime。

该commit点的H metadata inventory为 **20 exported types / 221 public member rows**；R0为22 / 228。这个总差额
包含R0之后的其他Hosting cut，不能全部归因于`9f5de810`。CLI build/online envelope仍序列化同一个
`RecapCompletionTelemetrySnapshot`，字段名与JSON shape不变。

### 3.4 Stop rule

本轮targeted CF-B到此停止。三个cut都有owner-local或snapshot-only的明确替代边界；继续为降低inventory count
而封闭output result algebra、删除external implementer contract或抽跨owner hierarchy，会进入已识别的
overreach/收益递减区，不再作为本轮候选。

## 4. CF-D-03 root config V1

### 4.1 Tracked contract

Galatea `config.json`现在是单一strict V1 language：

- root必须恰好包含raw integer token `"v":1`；missing、`null`、string、`0`、future value、`1.0`、`1e0`、
  wrong-case与duplicate均拒绝；
- first-party writer把`v`放在首字段，reader不要求property order；
- invalid version在connections、profile、prompt与route读取前失败；runtime `GalateaConfig`不携带version；
- existing config不会被bootstrap重写；没有versionless compatibility reader、dual parser或silent migration；
- production bootstrap使用`SerializeToUtf8Bytes`、单个LF与`WriteAllBytes`，不产生会被strict reader拒绝的
  UTF-8 BOM。

真实production bootstrap gate读取生成bytes，断言no BOM，执行`ValidateUsers`，再通过完整
`GalateaConfigLoader.Load`。另一正例把exact `v:1`移到非首字段，证明README的writer-order与reader-language
陈述不是同一个约束。

### 4.2 Content-free operator cutover

实际ignored operator manifest已在停服窗口人工加入`v:1`。记录的preflight/evidence为：

- target是regular file，mode `0600`、link count `1`；
- 当时没有Galatea process，TCP port `3510`没有listener；
- 删除新增`v`后的semantic content SHA-256为
  `0d45fa0c414b572c46e29893f3aa8d4eccd6b3b7c9c31030c3386a47692d6ba1`；
- provider-free loader成功，content-free结果为users `1`、connections `5`、`recapGrid=true`。

manifest仍由Git忽略；本批没有启动host/provider，也没有修改connections、profile、route或其他operator值。
这些事实证明current local manifest可被candidate loader接受，不构成跨部署兼容保证。

## 5. CF-C-01 Control classification

Control canonical state仍为Schema V2。`8a2186f8`只在decode前读取足以稳定分类的root discriminator：完整strict
JSON、首字段raw-exact unescaped `schemaVersion`、Int32 integer且不是V2、顶层没有duplicate或case-confusable
property时，映射为typed `ControlUnsupportedSchemaException`，并贯穿create/open/reader/snapshot、mutation、
inspect/verify/export/backup/restore/reinitialize现有operator result。

malformed/trailing JSON、escaped或wrong-case discriminator、discriminator非首字段、string/null、fraction、exponent、
overflow、duplicate/case-confusable root property，以及invalid V2 state仍保持Invalid/Corruption路径。classification
不把future bytes接受为current state，也不写回、迁移或reinitialize文件。

新增empty whole-state literal golden独立写出完整expected JSON与digest，再验证decode/re-encode byte exact；它不以
production writer自身作为唯一oracle。History/Store/Rewriter的同类independent evidence仍留给CF-C-02。

## 6. 分时验证

| Candidate point | Gate | Result |
|:--|:--|:--|
| `f1a8da0b` HistoryTimeline cut | `SessionJournal.HistoryTimeline.Tests` full | 172 / 172 passed |
| `f1a8da0b` HistoryTimeline cut | HistoryTimeline PublicSurface | 6 / 6 passed |
| `9f5de810` Hosting cut | Hosting owner tests / PublicSurface | 15 / 15；4 / 4 passed |
| `9f5de810` Hosting cut | focused CLI / Galatea rolling consumers | 1 / 1；5 / 5 passed |
| `8f72cb66` D03 tail | `GalateaConfigValidationTests` / Galatea full | 16 / 16；146 / 146 passed |
| `8f72cb66` D03 tail | `Atelia.sln` serial build | 0 warnings / 0 errors |
| `8a2186f8` CF-C-01 | Control full / Control PublicSurface | 48 / 48；3 / 3 passed |
| `8a2186f8` CF-C-01 | WalkingSkeleton / Galatea.RecapGrid owner gates | 27 / 27；7 / 7 passed |
| `8a2186f8` CF-C-01 | `Atelia.sln` serial build | 0 warnings / 0 errors |

这些结果来自各package closure时点，不是对`8f72cb66`做的一次所有测试全量重跑。D03 no-BOM red gate在修复前
确实因生成bytes以UTF-8 BOM开头而失败，修复后同一真实bootstrap gate通过。

## 7. Route adjustment与remaining boundary

- `CF-D-03`、本轮targeted `CF-B`与`CF-C-01` candidate implementation/package-local R4完成；保持
  **Prototype candidate**。
- broad CF-B停止；没有好候选时不为了计数继续扩大public API重写。
- 下一自然包是`CF-C-02`：为History/Store/Rewriter分别补independent golden/fingerprint与classification evidence，
  不预设一定产生code或wire change。
- CF-C-02之后整理current public support map、wire inventory、compatibility/upgrade policy与candidate commit map，
  进入R5 preparation。
- R5仍Pending；最终tier stable/frozen声明、tag与部署边界需要单独批准。
