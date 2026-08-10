# DerivedRecap Grid WP-07A：CLI 与 Operator Vertical Candidate

状态：Planned；依赖 WP-01..06 complete

只需加载：目标设计、Master、WP-06 handoff、本文与WP-07B摘要。

## Intent

用disposable repository把Timeline/Control/Grid/Manager/Composer串成可操作但不替换production default的CLI vertical；先锁
read-only/no-secret diagnostics与各durability domain的destructive authority。

## In scope

- `timeline inspect/export/verify/backup/restore/abandon`；
- `recap-control inspect/export`、register Family/Definition/Recipe、explicit activate CAS；
- `recap-control verify/backup/restore/reinitialize`直接映射WP-02 typed library actions；normal Control无普通reset，corrupt current不能在线
  restore/reinitialize，必须走offline exact archive/delete+Create；
- `recap-grid inspect/export/verify/reset`；
- build recipe/candidate fulfill/full rebuild commands；
- materialization inspect的strict `--nth-previous`；
- bounded progress：active recipe、Timeline head、fulfilled-through、missing assignments；HistoryLoad仅表示Timeline cadence；
- current CLI自写parser的strict command/confirmation/error mapping。

WP-01C已经提供typed library actions：`OpenReader/Inspect/Verify/Backup/Restore/Abandon`。本包只做System.CommandLine/文本
映射与operator confirmation，不重开backend选择、codec、path discovery或transaction；CLI normal discovery只用canonical locator，
不能扫描orphan/backup/latest。Restore要求current canonical schema/head仍可读且与manifest exact；head/schema不可读时只允许
explicit Abandon，不把restore降格成reset。

WP-03已经交付stable store-only顶层`recap-grid inspect|export|verify|reset`和typed library mapping，且没有接provider；本包只在
完整operator vertical中复用/验收该surface并补Manager/Getter commands，不重写Store path、witness、cursor或error taxonomy。

## Candidate boundary

CLI入口必须是明确candidate command group，不能成为long-livedfeature flag。diagnostic命令read-only/no-create且Completion
factory throwing；destructive action要求exact store/timeline scope确认。Grid reset只删Grid，raw/Timeline/Control bytes exact不变；
Timeline abandon是另一条高权操作，不能借Grid reset触发。
Control V1同样为Linux-only；CLI只映射typed platform/schema/busy/stale结果，不增加弱durability fallback或扫描backup/temp/latest。
Control `CommitIndeterminate`/backup `PublishIndeterminate`必须显示intended与observed scope/head并要求重新inspect，不得自动重试写操作。

## Acceptance matrix

1. absent/existing inspect前后filesystem snapshot一致，正文默认隐藏；V1使用WP-03 code-owned page/error bounds，不宣称未实现的
   `--limit/--max-errors`；
2. Agent/developer注册definition、overlay/full/A-B recipe，build与activate分离；
3. same recipe restart只调用missing cells；partial candidate不出现在materialize；
4. strict nth=0/1/N、branch sibling、missing/damaged slot不跳邻居；
5. wrong confirmation、wrongRef/scope、unauthorized family、over budget零mutation；
6. closed-store Grid reset crash old-or-empty-valid，raw/Timeline/Control exact不变；
7. Timeline restore/abandon独立演练；
8. provider failure保留successful Cells但不commitpartial RowView；retry只补missing；
9. no active/nonempty BuildTarget明确raw-only；active unfulfilled/Invalid fail closed。

## No-Go

- diagnostic创建Store或加载provider/secret；
- CLI把Control/Grid/raw事实merge成“latest”；
- reset跨durability domain；
- candidate command变成新旧runtime长期switch。

## Done when

disposable CLI E2E、crash/confirmation/read-only gates、build/docs/diff和independent review green。

## Handoff to WP-07B

交付strict command surface、operator evidence、disposable repo/fake provider和两个Host都可复用的composition factory。
