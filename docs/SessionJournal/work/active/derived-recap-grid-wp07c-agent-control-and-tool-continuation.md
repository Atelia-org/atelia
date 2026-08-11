# DerivedRecap Grid WP-07C：Agent Control 与 Tool Continuation Candidate

状态：Ready；WP-07B complete handoff已取得两路independent GO

只需加载：目标设计、Master、WP-07B handoff、本文与WP-08摘要。

## Intent

在不切production default的前提下，给WP-07B disposable Online composition补齐Agent-facing Control capability、code-owned genesis
入口与ToolResult/ToolContinuation lifecycle。WP-07C不重写Manager/Runtime scheduler，不持久化promotion proof，也不把operator fixture
冒充Agent tool。

## Locked scope

- 定义唯一Agent-facing provider-neutral control tool/capability，显式覆盖Family、Definition、Recipe登记与candidate promote；输入只接受正式
  canonical values或code-owned built-in asset ID，全部经过Control admission/allowlist/cap/budget复验；
- code-owned built-in genesis必须由明确operator命令生成canonical assets并显式provision；normal Galatea/CLI Host缺Timeline/Control/Store时
  fail closed，绝不auto-create；
- ToolResultObserved/ToolExecutionStarted恢复沿SessionJournal existing operation ID/sequence authority；composite lifecycle只在
  safe unprepared boundary执行bounded reconcile/seal -> fulfill -> readiness，并保持Prepared/Started frozen zero-active-derived；
- A/B candidate build与promotion分离：tool只提交Control transaction；需要promotion时fresh same-head proof由Manager重证，proof不编码、不落盘；
- Galatea与candidate CLI继续复用WP-07B Online+Hosting owner，不建立第二registry、scheduler、raw audit或Control backend；
- current production、old DerivedRecap projects与public Galatea constructor/DI保持不变，直到WP-08 atomic cut。

## Acceptance

1. built-in canonical assets golden、operator provision、normal missing-state fail closed；
2. Agent创建`XSuspicion` definition/overlay，unauthorized family/carrier/prefix/budget零mutation；
3. duplicate/replayed operation ID幂等，same expected Control CAS竞争仅一胜；
4. ToolExecutionStarted restart、ToolResultObserved多次safe lifecycle、caller cancel与raw/control/timeline drift；
5. Prepared/Started删除或损坏active Grid/control/routes仍按frozen bytes恢复，active collaborators调用数为零；
6. explicit candidate build -> zero-call revalidation -> promotion；partial/stale/missing无activate；
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
