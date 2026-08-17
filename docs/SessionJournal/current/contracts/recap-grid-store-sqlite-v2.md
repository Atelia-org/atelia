# RecapGrid Store SQLite V2 logical-schema candidate

状态：**post-tag approval candidate；尚未批准**  
适用product source：`cd966fc7fddfa6acbda6f80431cf9b588177d969`  
不属于immutable tag：`session-journal-contract-r2-approved-surfaces-v1`

本文把当前Grid Store SQLite V2的logical schema、canonical payload边界与operator分类整理成可审阅的
exact appendix。它不改变product、schema或accepted language，也不把候选提升为stable/frozen。

## 1. Authority与slot identity

唯一DDL owner是embedded resource
[`SchemaV2.sql`](../../../../prototypes/SessionJournal.RecapGrid/Store/SchemaV2.sql)；本文只给审阅用shape，
不复制完整DDL或建立第二个可执行schema truth。runtime validation与operator mapping由
[`SqliteRecapGridStore`](../../../../prototypes/SessionJournal.RecapGrid/Store/SqliteRecapGridStore.cs)、
[`StoreRuntime`](../../../../prototypes/SessionJournal.RecapGrid/Store/StoreRuntime.cs)及
[`StoreMaintenance`](../../../../prototypes/SessionJournal.RecapGrid/Store/StoreMaintenance.cs)拥有。

| Identity axis | Exact V2 value |
|:--|:--|
| repository-relative slot | `derived/recap-grid/v1/grid.sqlite` |
| SQLite `application_id` | `0x41544752` (`1096042322`) |
| SQLite `user_version` | `2` |
| logical schema objects | exactly five user tables below；no user index/trigger/view object |
| metadata | exactly one `store_metadata` row，`singleton=1`、`schema_version=2`、32-char lowercase hex `store_instance_id`，four nonnegative counters |

Open首先独立读取`user_version`；任何非`2`值优先分类为future/unsupported，即使application ID、metadata或
catalog同时损坏。current V2随后要求exact application ID、exactly one metadata row、以及按`type,name`
ordinal排序后与owner SQL materialize所得的五个`(type,name,tbl_name,sql)`完全相同；extra、missing或不同SQL
都不是V2。

## 2. Exact logical schema shape

所有表都是`STRICT`。除`store_metadata`外，四个artifact/locator表都是`WITHOUT ROWID`。下表中的列顺序、
type、nullability、key、CHECK、UNIQUE与FK均是V2的一部分；完整可执行文本仍只由owner SQL拥有。

| Table | Columns in ordinal order | Exact constraints and foreign keys |
|:--|:--|:--|
| `store_metadata` | `singleton INTEGER`；`schema_version INTEGER NOT NULL`；`store_instance_id TEXT NOT NULL`；`cell_count INTEGER NOT NULL`；`row_view_count INTEGER NOT NULL`；`row_view_member_count INTEGER NOT NULL`；`fulfilled_view_count INTEGER NOT NULL` | PK `singleton`且`CHECK(singleton=1)`；`CHECK(schema_version=2)`；four counters各自`>=0` |
| `cell_artifact` | `cell_digest TEXT`；`evaluation_key_digest TEXT NOT NULL`；`history_segment_digest TEXT NOT NULL`；`logical_column_id TEXT NOT NULL`；`definition_digest TEXT NOT NULL`；`content_digest TEXT NOT NULL`；`canonical BLOB NOT NULL` | PK `cell_digest`；UNIQUE `evaluation_key_digest`；UNIQUE `(cell_digest,logical_column_id,definition_digest)` |
| `row_view` | `view_digest TEXT`；`ref_id TEXT NOT NULL`；`timeline_id TEXT NOT NULL`；`history_row_id TEXT NOT NULL`；`row_descriptor_digest TEXT NOT NULL`；`recipe_digest TEXT NOT NULL`；`target_digest TEXT NOT NULL`；`previous_history_row_id TEXT NULL`；`previous_view_digest TEXT NULL`；`bootstrap_completed INTEGER NOT NULL`；`canonical BLOB NOT NULL` | PK `view_digest`；bootstrap仅`0/1`；previous row/view必须同时null或同时non-null；UNIQUE `(ref_id,timeline_id,recipe_digest,history_row_id)`、`(view_digest,ref_id,timeline_id,recipe_digest,history_row_id,target_digest)`、`(view_digest,ref_id,timeline_id,recipe_digest,row_descriptor_digest)`；self-FK `(previous_view_digest,ref_id,timeline_id,recipe_digest,previous_history_row_id,target_digest)`引用对应view/history-row scope |
| `row_view_member` | `view_digest TEXT NOT NULL`；`column_ordinal INTEGER NOT NULL`；`logical_column_id TEXT NOT NULL`；`definition_digest TEXT NOT NULL`；`cell_digest TEXT NOT NULL` | PK `(view_digest,column_ordinal)`且ordinal `>=0`；UNIQUE `(view_digest,logical_column_id)`；FK `view_digest`→`row_view`；FK `(cell_digest,logical_column_id,definition_digest)`→`cell_artifact` |
| `fulfilled_view_ref` | `ref_id TEXT NOT NULL`；`timeline_id TEXT NOT NULL`；`timeline_head_generation INTEGER NOT NULL`；`through_row_descriptor_digest TEXT NOT NULL`；`recipe_digest TEXT NOT NULL`；`key_canonical BLOB NOT NULL`；`view_digest TEXT NOT NULL` | generation `>=0`；PK `(ref_id,timeline_id,timeline_head_generation,through_row_descriptor_digest,recipe_digest)`；FK `(view_digest,ref_id,timeline_id,recipe_digest,through_row_descriptor_digest)`→matching `row_view` scope/descriptor |

## 3. Canonical payloads、versions与bounds

Canonical accepted language由
[`ArtifactContracts`](../../../../prototypes/SessionJournal.RecapGrid/Abstractions/ArtifactContracts.cs)和
[`RecapGridLimits`](../../../../prototypes/SessionJournal.RecapGrid/Abstractions/RecapGridSyntax.cs)拥有；
[`CanonicalContractTests`](../../../../tests/SessionJournal.RecapGrid.Abstractions.Tests/CanonicalContractTests.cs)
锁exact round-trip、version prefix、domain separation与goldens。

| Stored canonical | Exact version / fields | Bounds and digest domain |
|:--|:--|:--|
| `cell_artifact.canonical` | JSON v1：`schemaVersion,cellDigest,logicalColumnId,definitionDigest,evaluationKey,outcome,content,contentDigest`；`outcome`只有`updated`或`keep-unchanged` | content UTF-8最多1,048,576 bytes；whole canonical最多1,179,648 bytes；content domain `atelia.recap-grid.content.v1`，cell domain `atelia.recap-grid.cell.v1` |
| `row_view.canonical` | JSON v2：`schemaVersion,digest,refId,timelineId,historyRowId,rowDescriptorDigest,recipeDigest,targetDigest,previousHistoryRowId,previousViewDigest,bootstrapCompleted,orderedCells[]`；member为`logicalColumnId,definitionDigest,cellDigest` | 最多128 ordered cells；whole canonical最多524,288 bytes；digest domain `atelia.recap-grid.row-view.v2` |
| `fulfilled_view_ref.key_canonical` | JSON v1：`schemaVersion,refId,timelineId,timelineHeadGeneration,throughRowDescriptorDigest,recipeDigest` | generation nonnegative；whole canonical最多16,384 bytes |

`evaluationKey`使用其owning canonical bytes；本appendix不扩张该嵌套contract。decode要求strict exact canonical
re-encode equality，而不是接受等价JSON变体。

## 4. Indexed locator proof

SQLite中的denormalized columns是query、scope与corruption proof，不是第二份artifact authority：

- Cell decode后逐项核对digest、evaluation-key digest、history-segment digest、logical column、definition与content digest；
- RowView decode后逐项核对scope/digests/previous/bootstrap，member ordinal必须从0连续，member tuple必须与canonical
  ordered cells及所引用Cell一致；
- fulfilled key decode后必须与requested exact key canonical相同，并核对locator scope及所引用RowView的
  `view/ref/timeline/recipe/row-descriptor`；
- metadata counters在write transaction内维护，full verify会重数并检查全图；它们是corruption proof，不覆盖tables。

因此不可通过删除indexed locator/FK/counter来“去重”；那会删除bounded lookup或independent corruption evidence。

## 5. Persistent PRAGMAs与independent fingerprint

V2 create并在open时复核两个persistent invariants：

| PRAGMA | Exact accepted value |
|:--|:--|
| `page_size` | `4096` |
| `journal_mode` | `delete` |

这些值不承诺SQLite physical bytes determinism。`foreign_keys`、`synchronous`、`trusted_schema`、`busy_timeout`、
`temp_store`、`locking_mode`、`read_uncommitted`、`query_only`与`max_page_count`等connection/runtime policy不属于
本logical-schema candidate。

Test-owned
[`CreatedStoreMatchesIndependentV2LogicalSchemaFingerprint`](../../../../tests/SessionJournal.RecapGrid.Store.Tests/StoreAuthorityRegressionTests.cs)
独立读取application/user version、metadata shape和ordered five-row `sqlite_schema` transcript；SHA-256为
`3b14f5e58f4012f699b9314b96f145dc43e878fbbc7e8d25574991319281343c`。persistent PRAGMA断言独立于该
transcript，因此新增gate不改变既有logical-schema fingerprint。

## 6. Operator classification与upgrade policy

| Disk fact | `Create` existing | `Open` / `OpenReader` / `Inspect` / `Export` | `Verify` |
|:--|:--|:--|:--|
| valid exact V2 | `AlreadyExists`（同一exclusive lease内先验证） | ordinary opened/available/page | `Healthy` |
| `user_version != 2` | `Invalid(code=GridStoreUnsupportedSchema)` | typed `UnsupportedSchema(actualVersion)` | typed `UnsupportedSchema(actualVersion)` |
| current-version identity/catalog/metadata invalid | `Invalid(code=GridStoreInvalid)` | typed `Invalid(code=GridStoreInvalid,...)` | `Unhealthy(Incomplete=true, nonempty errors)` |

schema/version mismatch或corruption都不触发fallback、auto-repair、silent migration或existing-create rewrite。
升级必须建立新version/candidate与显式migration proof，或在raw/authoritative inputs保留的前提下显式reset/reprovision。
physical reset继续受witness与commit-indeterminate contract约束。

## 7. Explicit non-promises

本candidate不批准、也不承诺：

- SQLite database/file/page的byte identity、file length、page allocation、freelist、VACUUM结果或backup byte identity；
- SQLite runtime/source ID、compile options或等价logical state的physical layout；
- connection-local PRAGMA值作为cross-version wire；
- 新旧schema dual reader、automatic migration/repair，或未列入本appendix的future fields/indexes；
- hostile out-of-band writer、filesystem/platform行为超出现有Store durability/operator contract的保证。

批准本appendix必须是后续显式user decision；其状态不能由测试通过、文档合入或既有surface-set-1 tag自动提升。
