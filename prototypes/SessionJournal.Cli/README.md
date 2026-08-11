# SessionJournal.Cli

DerivedRecap当前operator surface：

```text
recap planner-config init|inspect
recap history-load inspect
recap create
recap inspect
recap materialize-inspect
recap run
recap rebuild --campaign <id> [--reset --confirm-ref <ref-id>]
recap reset --confirm-ref <ref-id>

recap-grid inspect|verify --input <repo-dir>
recap-grid export --input <repo-dir> [--after <opaque-cursor>] [--include-content]
recap-grid reset --prepare --input <repo-dir>
recap-grid reset --input <repo-dir> --confirm-length <bytes> --confirm-sha256 <sha256>

recap-grid candidate init ...
recap-grid candidate timeline create|sync|inspect|verify|export|backup|restore|abandon ...
recap-grid candidate control create|inspect|verify|export|put-family|put-definition|put-recipe|activate|promote|backup|restore|reinitialize ...
recap-grid candidate build|progress|materialize ...
```

`recap-grid` 是Derived Recap Grid rewrite的candidate operator surface，尚未切换
Galatea/current production。`reset --prepare`只读输出当前exact physical
`length`/`sha256`；将这两个值原样传给reset确认。inspect/export/verify/reset使用
code-owned分页与错误上限，首版不提供`--limit`或`--max-errors`选项。

完整Grid operator vertical仍只在明确的`recap-grid candidate`子树中。所有branch mutation都要求与selected
SessionJournal branch相同的`--confirm-ref`；`init`显式按Timeline、Control、Grid三域创建，其他命令不自动创建。
Family/Definition/Recipe输入必须是各自formal canonical bytes；Control admission是独立strict V1文件，不能从payload自授权。
只有`candidate build`会读取strict route manifest与Completion connections：route按
`(FamilyDigest, RuntimeProtocolId, SemanticModelId?)` exact匹配，显式`null`也不fallback；connections在Manager/Store/provider前执行
whole-file、count、field strict UTF-8 bounds以及duplicate/unknown/BOM检查。`progress`不构造provider；
`promote`在同一进程用`--max-new-calls 0`重证head-through proof后才执行Promotion CAS，build本身永不activate；
`materialize`只走Getter strict `--nth-previous`，不在CLI拼raw tail。build report附带bounded operational call evidence；no-work与
其他provider-free命令不materialize该collector。

candidate JSON report使用`atelia.session-journal.recap-grid-candidate-cli.v1`；syntax/confirmation返回1，typed operational failure
返回2，success/idempotent返回0。Busy/Stale/Unsupported/Indeterminate均不自动retry。该candidate尚待独立review，并未切换
Galatea、`run-online-turn`、旧`recap`命令或current production。

旧 `resume`、`restore`、`abandon-building`命令已删除。`recap run`内部自动优先恢复Building或修复
Published，随后才惰性加载active config并规划新shared epoch。正常run超过bounded raw authority只返回
FullRebuildRequired，不创建spool。

`recap rebuild`是显式operator path：首次用`--reset --confirm-ref`在sealed raw audit完成后重置v8
truth Store；若返回MoreWorkPending，使用相同campaign且不再传`--reset`继续。campaign绑定exact
RefId/head；head变化fail closed。

`planner-config init`写canonical v3到`config/recap-planner-config.json`；inspect严格拒绝旧schema、
unknown/duplicate字段和非canonical bytes。frozen recovery不依赖该文件可用。

`run-online-turn`使用同一个`DerivedRecapOnlineLifecycleCoordinator`和deferred Maintainer registry；
NoBuild、全healthy Resume或选择失败前不会构造provider client。R6 production-composition acceptance已覆盖
Galatea real-session lifecycle与CLI exact-ref reset、多operation/multi-epoch rebuild、per-member attribution和
raw/non-derived bytes不变。R7 official-provider canary因TLS/authentication环境失败而没有response usage，
因此真实cache write/read与经济性结论仍为`Environment-blocked`。
