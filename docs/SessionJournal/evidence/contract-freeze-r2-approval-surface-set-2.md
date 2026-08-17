# SessionJournal Contract Freeze R2 — additive surface set 2 approval

状态：**approval complete；annotated surface set 2 tag anchored**  
validated product source：`8c450bf03f58cb62753d8b3732e66adae36b1809`  
integration evidence：`6c5d3d50e68b84b9dca1391c16438a86cef418c1`  
promotion docs commit：`a07de1f40b5f`  
approval tag：`session-journal-contract-r2-approved-surfaces-v2`  
tag object：`13111f3df6c74813e7e47673be7e9d0a1c1309ee`  
dereferenced target：`c4c6dd1698c7460fbf8ff3563d7800203f3202e0`  
记录日期：2026-08-17

本文只记录用户在immutable
`session-journal-contract-r2-approved-surfaces-v1`之后明确批准的两个additive surfaces。surface set 2不替换、
移动或重新解释surface set 1；v1 tag继续只认证其原始promotion docs与product source `cd966fc7`。

## 1. Approved additive surfaces

| Tier / surface | Exact approved scope | Upgrade / failure policy |
|:--|:--|:--|
| Tier B — [RecapGrid Store SQLite V2 logical schema](../current/contracts/recap-grid-store-sqlite-v2.md) | repository slot、application/user version、exact five-table logical shape与metadata；stored canonical payload/version/bounds；indexed locator/FK/counter proof；persistent `page_size=4096`与`journal_mode=delete`；appendix §6的Create/Open/OpenReader/Inspect/Export/Verify operator mapping | future version typed Unsupported；current V2 corruption Invalid/Unhealthy；explicit reset/reprovision或另立version/migration proof；无auto-repair/dual reader |
| Tier C — [Galatea root config V1](../current/contracts/galatea-root-config-v1.md) | strict V1 JSON accepted language；root/user/recapGrid required/optional/count rules；prompt-file precedence；config-directory-relative path与absolute-target semantics；profile/route dependencies；root/prompt/profile bounds；bootstrap only-if-missing、no-BOM与existing-file no-rewrite policy | versionless/future/invalid fail closed；停服、备份、确认actual config path后显式升级；无CWD/existence fallback、auto rewrite/move或silent migration |

Store批准的是logical SQLite contract，不是SQLite physical representation。Root批准的是exact file language与operator
policy；它不吸收Completion connections、Route manifest或AgentControl profile各自已批准的owner contracts。

## 2. Explicit non-promises

Surface set 2明确不批准：

- Store database/page/file byte identity、allocation、freelist、VACUUM/backup byte identity、SQLite runtime/source ID、
  compile options或未列出的connection-local PRAGMAs；
- root password/secret at-rest protection、redaction或secret-store integration；bootstrap file mode、ownership、ACL或
  permissions enforcement；
- `listenUrls`的Kestrel parsing/binding、TLS、port availability或network exposure；
- exception/diagnostic逐字文本，以及path/IO/permission/owner-registry低层异常的统一包装或稳定type；
- provider construction/content quality、real deployment readiness、ignored operator state或historical operator evidence renewal；
- root bootstrap的byte identity、whitespace/property order/escaping/newline formatting；auto migration/rewrite、session
  create/move、path confinement或完整hostile-filesystem defense；
- 任何未在§1精确列出的Tier B/C surface、blanket CLR public API、physical RBF或surface set 1既有non-promise。

## 3. Evidence与verification boundary

- Store owner source仍来自surface-set-1 product line；appendix commits `43e9ce9a` + `d012ceaf`登记independent
  ordered five-row `sqlite_schema` fingerprint（SHA-256
  `3b14f5e58f4012f699b9314b96f145dc43e878fbbc7e8d25574991319281343c`）、persistent-PRAGMA与validation
  precedence。Stored canonical digest domains包括`atelia.recap-grid.content.v1`、`atelia.recap-grid.cell.v1`与
  `atelia.recap-grid.row-view.v2`；validated product source统一pin到`8c450bf0`；
- root path implementation为`0f0afb2c`，field/classification gates为`0515083f`与`8c450bf0`；test-only
  `6c5d3d50`通过full `GalateaConfigLoader`锁duplicate `ProfileId` / `RuntimeIdentity` conflict；
- owning exact oracles是
  [`StoreAuthorityRegressionTests`](../../../tests/SessionJournal.RecapGrid.Store.Tests/StoreAuthorityRegressionTests.cs)、
  [`GalateaRootConfigFieldLanguageTests`](../../../tests/Galatea.Server.Tests/GalateaRootConfigFieldLanguageTests.cs)与
  [`GalateaConfigValidationTests`](../../../tests/Galatea.Server.Tests/GalateaConfigValidationTests.cs)；
- 本promotion只修改文档；由于root含post-v1-tag production delta，tag前统一验证已在exact code/test source
  `6c5d3d50` + docs-only promotion HEAD `a07de1f4`上重新串行完成，不使用historical v1 green evidence替代；
- public inventory与disposable rebuild为**NotRun / 本次无需**：surface set 2不批准.NET API且没有raw/rebuild contract delta。
  provider/deployment、ignored operator config仍不读取、不运行，也不从tag前gates推导。

### 3.1 Unified pre-tag gate ledger

| Gate | Result at `a07de1f4` |
|:--|:--|
| Store full | 54 / 54 passed |
| Galatea full | 162 / 162 passed |
| `dotnet test Atelia.sln --no-restore -m:1 -nr:false` | 38 projects / 4,694 passed / 0 failed / 0 skipped |
| `dotnet build Atelia.sln --no-restore -m:1 -nr:false` | 0 warnings / 0 errors；15.25 s |
| production HTTP Node contract suite | 1 passed / 0 failed |
| production SSE Node contract suite | 1 passed / 0 failed |
| scoped docs / repository diff | 18 files / 0 diagnostics；diff/status clean |
| independent pre-tag docs review | PASS |
| public inventory / disposable rebuild | NotRun / 本次无需 |

最初两条Node命令误写了不存在的`Browser`子目录，均立即以file-not-found退出、没有启动test；随后使用correct
production test paths重跑，HTTP与SSE各1/1通过。该calibration不计作product failure，也没有被省略。

## 4. Tag closure record

1. containing promotion docs commit `a07de1f4`已产生；
2. exact code/test source `6c5d3d50` + docs-only promotion HEAD上的Store/Galatea/full solution/Node/docs gates与
   independent scope review已按§3.1通过；
3. annotated tag已exact创建为`session-journal-contract-r2-approved-surfaces-v2`；tag object为
   `13111f3df6c74813e7e47673be7e9d0a1c1309ee`，dereferenced target为包含final gate ledger的
   `c4c6dd1698c7460fbf8ff3563d7800203f3202e0`；
4. verified tag message pin product source `8c450bf0`、code/test source `6c5d3d50`、promotion draft `a07de1f4`、
   cumulative approved scope/non-promises与prior immutable v1 target `6378cebb`；
5. v1 dereferenced target仍为`6378cebbde4cf150ecb4d8de5699ef1f77ce4f0b`，没有移动。

本post-tag status tail只记录已经发生的tag closure；tag继续指向`c4c6dd16`，不会因当前或未来文档commit而反向移动、
续期product/deployment evidence或扩大§1批准范围。
