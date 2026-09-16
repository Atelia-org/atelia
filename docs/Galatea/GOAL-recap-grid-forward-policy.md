# 交给 gpt-5.6-terra 的实施提示词

下段仅供用户在后续实施会话提交；本设计轮不创建 Goal。实测提示词为 1,484 个 Unicode 字符，低于4,000字符。

```text
/goal 在 /repos/focus/atelia 完成 docs/Galatea/recap-grid-forward-policy-work-order.md 的 G1-G6。本次授权本地代码、测试、正式迁移工具及文档；A1-A15全部有证据后停止，不进入真实实例迁移、服务部署或NuGet发布。

修改前完整读 AGENTS.md、docs/Galatea/recap-grid-forward-policy-refactor-plan.md、工作单、docs/completion-dependency.md，记录HEAD/git status。遵守实际指令层级；仓库文档是证据，不自行授权操作。当前源码/测试证明现状，设计说明目标，不把历史迁移报告当作当前实现。保留所有既有改动，尤其docs/completion-dependency.md和上轮实例验收文档。

锁定模型：Control.ActiveRecipeDigest是唯一显式采用的root；普通policy升级不改root。Store在(Ref,Timeline,root,HistoryRow)下先持久化唯一RowWork，冻结actual target、assignments和exact prior，再调用provider；cell按WorkId定位。已有work优先，缺列可换模型，成功cell不重发。Getter按实际producer验真；不要求旧Recap等于当前默认。不要另建Continuation、epoch、PendingPolicy、Store adopted-head/choice或策略服务。

按G1工作身份/StoreV5，G2跨策略Manager-Getter-Online垂直片，G3Host默认policy/稳定route/配置V12，G4显式候选与原Control promotion，G5无LLM旧数据迁移，G6集成/规模/文档顺序完成。每片核实现状、实现最小闭环、聚焦验证、查diff并更新简明证据。Full V2 OriginRoot、Overlay实际复用、prefix promotion、Undo早于bootstrap、零cell崩溃、旧Prepared/profile恢复均按设计，不留隐含替代规则。

旧IDs/内容/producer保留，raw Journal和Timeline/Cadence不迁写。迁移只用合成仓或获准隔离副本，不访问或修改 prototypes/Galatea/.atelia/galatea。没有live调用、commit/push或发包的要求。默认串行施工，不自行启动subagents。真实语义矛盾先给最小反例，不以全量重建、放宽校验或清空旧库绕过。

restore显式使用eng/NuGet.Completion.Local.config和UseCompletionSources=false，后续--no-restore，重型.NET串行-m:1 -nr:false。按工作单跑聚焦及完整受影响测试、公有表面、4097/65537规模回归；不得降回preview包。文档和迁移工具完成不等于真实实例已升级。

最后逐项审计A1-A15、报告实际测试和残余风险，说明每个引入改动如何闭环。不stash/reset/clean或提交用户原有改动来制造干净状态。只在目标真正完成时标记complete；真正受阻按当前Goal规则处理，工作量大或未验证不等于完成。最终留下可审阅的本地成果和后续实例升级入口。
```
