# Desired setup reconciliation report V2 approved contract

状态：**user-approved Stable V2 operational receipt；surface set 3 unified gates/tag Pending**  
production source：`da3aa27af56add07bc70229120c522b8d24c99ba`  
test evidence：`8a54e613f7c1a92bab3a4dd0806aad19411c41b1`  
approval boundary：不属于immutable v1/v2 tags；authorized v3 tag
`session-journal-contract-r2-approved-surfaces-v3`尚未创建

本文定义`reconcile-desired-setup --report-json` current producer输出的exact V2 machine-readable shape与operator
consumption rule。用户已将[surface set 3 addendum](../../evidence/contract-freeze-r2-approval-surface-set-3.md#1-approved-additive-surface)
列出的narrow scope批准为Stable V2 operational receipt；unified gates与annotated v3 tag仍Pending。它是producer-only
report contract，不是raw authority、recovery proof或通用CLI envelope；源码与owning tests仍是实现事实。

## 1. Producer、schema与exact field set

唯一production producer是
[`DesiredSetupReconciliationCommand`](../../../../prototypes/SessionJournal.Cli/DesiredSetupReconciliationCommand.cs)，
field-set/type oracle是
[`ProgramDesiredSetupReconciliationCommandTests`](../../../../tests/SessionJournal.Cli.Tests/ProgramDesiredSetupReconciliationCommandTests.cs)。

Root必须是JSON object，`schema`必须exact等于
`atelia.session-journal.desired-setup-reconciliation.v2`，且必须恰有下列10个decoded property names；missing、extra、
wrong-case或wrong-type均不属于V2：

| Field | JSON type | Exact meaning |
|:--|:--|:--|
| `schema` | string | exact V2 identifier |
| `branchName` | string | 本次成功reconcile所open的exact branch name |
| `connectionId` | string | 从Completion-owned connections manifest exact选择的connection ID；不是provider secret或durable connection fingerprint |
| `beforeHead` | string | command在mutation前已验证等于`--expected-head`的exact current head；使用SessionJournal `EventAddress` text codec |
| `afterHead` | string | reconcile完成后重新读取并验证为Idle、且拥有final governing setup的exact current head |
| `runtimeConfigChanged` | boolean | mutation前governing `ModelId`或`CompletionSurfaceId`与desired connection不exact相等时为true |
| `systemPromptChanged` | boolean | mutation前governing system prompt与desired prompt不exact相等时为true |
| `modelId` | string | `afterHead` governing runtime config中、且已与selected connection复核的exact model ID |
| `completionSurfaceId` | string | `afterHead` governing runtime config中、且已与selected connection复核的exact completion surface ID |
| `systemPromptUtf8Sha256` | string | lowercase 64-hex `SHA256(UTF8(afterHead governing system prompt))`；V2不另带codec-id field |

若两个changed flags均为false，成功report要求`afterHead == beforeHead`；任一为true时raw setup append推进head。
Runtime config同时比较model/surface并只用一个flag表达；report不承诺event-count delta，也不复制preserved schema、
DerivedContext或setup event addresses。

## 2. Success、exit与publication ordering

Exit 0只在以下事实全部成立后返回：

1. paths validation与connections/prompt input reads成功；observed head exact等于`--expected-head`且处于Idle；
2. raw desired-setup reconciliation返回Ready；runtime config需要变更时先append，system prompt需要变更时后append；
3. command重新读取`afterHead`，复核Idle、Ready governing head与final governing setup exact匹配requested values；
4. 若提供`--report-json`，report已由`CliIo.WriteJsonAtomically`成功publish；然后才打印success summary并返回0。

Raw reconcile发生在atomic report write之前。Runtime与prompt同时变化时，第一个raw append可能已经durable，而第二个append、
post-reconcile verification或report publication仍可能失败。因此exit 1、missing report或未更新的旧report**都不证明raw没有
mutation**。Report是mutation之后的operational receipt，不是transactional commit record。

Production writer使用temporary file、flush-to-disk与atomic replace，并允许overwrite existing leaf。它不是create-only
API。Activation runbook要求report path预先不存在，是为了排除stale receipt与失败后歧义的operator precondition，
不是production writer的accepted-language限制。

## 3. Failure recovery与idempotent retry

遇到exit 1、missing/invalid V2 report或report publication不确定时，operator必须fail closed：

1. 不得继续使用旧`--expected-head`，也不得从missing report推断repository未变；
2. 重新只读inspect/validate current selected branch，取得observed exact head，并确认execution phase仍为Idle；
3. 在该exact head解析governing setup，核对current model、completion surface与system-prompt UTF-8 SHA-256；
4. 以新observed exact head重新执行同一desired intent。已durable的第一步会被exact comparison识别，reconcile只补缺失
   setup或返回两项unchanged；不要手工append、rollback raw或盲重放旧expected-head。

这一路径利用`ReconcileDesiredSetup`的exact-head/CAS与idempotent equality semantics；report本身不授权retry，也不取代
current raw head、Idle boundary或governing setup inspection。

## 4. JSON representation与consumer rule

V2冻结field set、JSON types与field meanings，但不承诺serializer property order、whitespace、indentation、escaping、
terminal newline或byte identity。Consumer必须按decoded exact names验证schema、exact 10-field set与types；不得依赖当前
record declaration order，也不得对unknown/missing field做best-effort fallback。

仓内没有production report reader，也不提供V1/V2 dual reader、compat parser或dual writer。Current producer只写V2；
runbook consumer使用explicit `jq` gate。需要改变field set/type/meaning时必须建立新schema/candidate并原子更新producer与
first-party consumers，不能在V2下additive塞field。

## 5. Privacy与explicit non-promises

Report不包含system-prompt text、base address、API key、env secret locator、connections path、prompt path或repository path。
它包含branch/connection/model/surface identifiers、raw head addresses与prompt hash；这些仍是operational metadata，prompt
hash可能泄露相等性或可猜文本，不应自动视为可公开或无敏感性。

本contract不承诺：

- `--system-prompt-file`的decode/BOM/newline/`Trim()`等input semantics、connections manifest language或其他CLI inputs；
- prompt text、provider request/content、endpoint/secret、deployment success或real-provider readiness；
- stdout/stderr、human summary、exception/diagnostic逐字文本、exit 1的细分类或stack trace；
- report filename/path、create-only behavior、file permissions/ownership、cross-filesystem durability或hostile same-directory writer；
- 将report作为raw mutation absence proof、recovery authority、governing setup authority或跨command generic envelope；
- V1 compatibility、unknown-field tolerance、canonical JSON bytes或任何未列入本文的future field。

该appendix形成于immutable surface-set-2 tag之后；用户随后已明确批准additive surface set 3的exact narrow scope，
但该批准不移动或重释v1/v2 tags，也不把本节non-promises纳入承诺。Containing promotion docs、unified gates与
annotated v3 tag仍Pending；tag创建前不得将本文件表述成已anchored，tag创建后也只能认证addendum精确列出的scope。
