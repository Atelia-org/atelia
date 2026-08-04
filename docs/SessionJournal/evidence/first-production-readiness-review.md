# SessionJournal 首次生产运行前综合审阅报告

状态：R0–R4 closed；Beta GO  
Review baseline：`2ccd67150373360a1230dd22c195b4ec100ac0bb`  
Product candidate：`681fc02bb9f1e4a45cd012aa7feadefe3f33fa9e`

本文是
[`session-journal-first-production-readiness-review-plan.md`](../session-journal-first-production-readiness-review-plan.md)
的综合结论。原始盲审报告和 run-specific 验收证据保存在
`gitignore/session-journal/reviews/2026-08-02-production-readiness/`，不作为 contract authority。

## 1. Decision summary

R1 的十份独立只读盲审没有发现 P0，主线程在 R2 确认九个 Beta blocker，并于 R3 逐包修复、测试、独立复核。
当前没有未处理的 P0/P1，确认的 P2 已修复或写明 accepted/deferred 边界。

R4 在两个相互独立的 `--no-local` fresh clone 上完成。两次均精确检出同一 product candidate，串行通过
build、完整 suites、真实数据、fresh recap、NoBuild、disposable provider canary、reopen/Undo 与 invariants。
因此结论为 **Beta GO**，适合开始一次有监控、可停机、可备份的首次生产运行。

## 2. Confirmed findings and dispositions

| ID | Severity | Confirmed claim | Minimal correction | Closure | Result / residual boundary |
|---|---|---|---|---|---|
| B-RAW-1 | P1 | raw envelope/body/nested JSON 的 exact 与 semantic acceptance 不一致，Offline 可认证 writer 不会产生的 raw | 统一 strict decode、domain validation 与 Prepared full validation gate | `04e7146f`, `23e16a17` | Closed；未知、重复、错型、越界与 commitment mismatch fail closed |
| B-PREP-1 | P1 | Host 可在 full reconstruction 前取得坏 Prepared 的 runtime identity | inspection 与 Resume 共享同一 Prepared validation barrier | `04e7146f`, `23e16a17` | Closed；不从 active config/default connection 补猜 |
| E-LIFE-1 | P1 | derived lifecycle callback 获得 writable Engine，可绕过 raw authority | 改为 engine-bound bounded read/lifecycle capability | `17dddbea`, `87aecb20`, `d95c24c1` | Closed；online mutation 仍要求 exact head 与 Host 串行化 |
| C-SEAL-1 | P1 | Building 的 derived publication seal 缺失/损坏后，missing-only recovery 无法重建 | 允许从 authority inputs 重封 candidate，再执行 promotion checks | `ca266d10` | Closed；不承诺复用损坏 bytes |
| C-HEALTH-1 | P1 | selection/materialization 与 planning 对 noncanonical publication 的健康判断分裂 | 单一 canonical publication health 规则 | `ca266d10` | Closed；`Selected` 仍只证明 metadata descriptor |
| C-READ-1 | P2 | 只读 selection/inspection 会创建 Store scaffolding/lock | zero-touch read path 与 typed unavailable | `90c28097` | Closed；`OpenReadOnly` 不执行 tail recovery |
| C/D-AUTH-1 | P1 | low-level Planner/Building/Store seams 可形成第二 authority | 关闭 union、internalize executors、绑定 Engine/Ref/lineage proof | `9cb0f29c`…`e2bf7a01`, `6221ecf7`…`3f2293ce` | Closed；final reread 是 fence，不是 CAS |
| D-MAINT-1 | P1 | Building 只冻结 `(MaintainerId, Target)`，同 ID 可混入两代语义 | 冻结并 exact bind capability fingerprint | `162208b5`, `888447e9` | Closed；Resume/Restore 不读取 active roster |
| E-LOG-1 | P1 | provider 成功后，call-log 失败会覆盖成功结果并诱发重试 | logging/report 变为 non-authoritative best effort | `f1d6a2fb`, `3cb98e07` | Closed；日志可缺失且不能证明调用未发生 |

同时完成的 P2/P3 收口包括：failed-turn 与 Idle 分离、exact failed-turn abandon、bounded lineage、
closed result unions、Planner config/catalog 单一 identity、`RawHistoryAuthorized` genesis-only 例外、
online report v6 content-free、Prepared v5 文档/golden、Galatea 同 Engine 驱动 gate 与 live/recent cache 隔离。

## 3. Rejected, accepted and deferred

- **Rejected — Parent cycle finding**：EventJournal writer 的 Parent 只能指向已存在且通过 checked-read 的 event；
  append-only 物理顺序排除 writer-produced cycle。full audit 仍保留 visited guard，不在每个 bounded walk 复制防线。
- **Accepted for first Beta — hostile concurrent path replacement**：Store 防静态 symlink/reparse，但 threat model
  限于单用户私有 repo；不声称抵抗拥有同目录写权限的并发攻击者。
- **Accepted limit — bounded prefix**：`BeyondPrefix` 不分页、不自动扩界、不 fallback 到 full scan；损坏 strict
  ordinal slot 不跳过、不重编号。
- **Accepted limit — provider side effects**：不承诺 exactly-once，也不承诺损坏 component 的 regeneration
  byte-identical；raw 与 frozen plan 决定恢复 authority。
- **Deferred P3**：全 public member XML docs、跨平台真实断电矩阵、任意第三方 Host 的完整认证、full scrub、
  tamper signature、backup 与 replication。

## 4. Repair commit map

| Work package | Commits |
|---|---|
| WP1 raw/recovery | `04e7146f`, `23e16a17` |
| WP2 Store health/read | `ca266d10`, `90c28097` |
| WP3 maintainer identity | `162208b5`, `888447e9` |
| WP4 authority/config | `9cb0f29c`…`e2bf7a01` |
| B0 bounded raw | `6221ecf7`, `72fed0b8` |
| B1 Store bounded lineage | `b854b3eb`…`21f7853e` |
| B2a frozen setup wire | `dc2fd978`…`fe370ddf` |
| B2b bounded online authority | `0c5b25ca`…`3f2293ce` |
| WP5 Host/public surface | `17dddbea`, `87aecb20`, `b7b2fa2c`, `9d808084`, `b6462721`, `d95c24c1` |
| WP6 operational seams | `f1d6a2fb`, `3cb98e07` |
| R4 expectation fix | `bc7ba07a` |
| Galatea live Engine gate | `bfae9263`, `455d9b19`, `681fc02b` |

表中的 `…` 表示同一工作包内的 inclusive commit range；逐提交清单保存在对应的 R2/B2 plan-lock
run evidence 中。

每组高风险修复均由未参与实现的 reviewer 复核；最终 Galatea 三提交复核结论为“无 findings”。

## 5. R4 evidence matrix

环境：Ubuntu 24.04 / WSL2，Linux `6.18.33.2-microsoft-standard-WSL2`，ext4，.NET SDK `10.0.110`。
Store durability 结论限定为 Linux；process-death CrashHarness 不等同于 physical power-loss 证明。

Clone A 使用 `git clone --no-local`，detached checkout exact candidate，restore/build 和所有测试严格串行。

| Gate | Clone A | Clone B |
|---|---|---|
| Solution Release build | 0 warnings, 0 errors | 0 warnings, 0 errors |
| `SessionJournal.Tests` | 392/392 | 392/392 |
| `SessionJournal.Offline.Tests` | 6/6 | 6/6 |
| `SessionJournal.DerivedRecap.Store.Tests` | 192/192 | 192/192 |
| `SessionJournal.DerivedRecap.Planner.Tests` | 260/260 | 260/260 |
| `SessionJournal.DerivedRecap.Maintainers.Tests` | 28/28 | 28/28 |
| `SessionJournal.Cli.Tests` | 98 passed, 1 expected external-fixture skip | 98 passed, 1 expected external-fixture skip |
| `Galatea.Server.Tests` | 59 passed, 4 expected staging skips | 59 passed, 4 expected staging skips |
| Seven default suites total | 1035 passed, 5 skipped, 0 failed | 1035 passed, 5 skipped, 0 failed |
| Prepared/Started/tool/failed reopen + unsupported Host capability typed rejection | representative deterministic matrix passed | representative deterministic matrix passed |
| Explicit real-data acceptance | 1/1 | 1/1 |
| Explicit disposable staging acceptance | 4/4 | 4/4 |

真实 source export 为 1,281,881 bytes，SHA-256
`b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3`。fresh import 得到
148 selected events、474,498 logical payload bytes、Idle head；strict validate 通过。config 初始化/解析、Store create、
real-provider recap publish、materialize 与 immediate NoBuild 均通过；NoBuild provider call count 为 0。
Recap 前后 raw events/refs fingerprint 都是
`d7384024d1f215e1262bf409cb22139314accee172fb130cec6e4747d1c5f431`，source bytes/hash 也未改变。

disposable Host canary 只发送一个真实 provider turn。并发轮询的 1200 组 current/recent HTTP 响应全部为 200；
该轮完成后 recent 可见 exact canary，call log 恰好 1 个 agent、0 个 maintenance。首轮轮询脚本没有保留中间
response body，因而真实 run 只证明 live 期间无 500；exact live turn id 由 deterministic tests 覆盖。

进程重开后 recent 与完成态逐字段一致；exact Undo 返回被移动的 canary turn。Undo 保留回合前写入的
`RuntimeConfigSetup`，因此最终 selected head 为 `ej1:00000497d00000410000000100000000`，149 events，
474,668 logical payload bytes，Idle；strict offline validate 再次通过。这是 selected Parent-lineage 的预期语义。

Clone A 的 content-free evidence 位于
`gitignore/session-journal/reviews/2026-08-02-production-readiness/r4-final/clone-a/`。

Clone B 重复了相同的 fresh import/recap/NoBuild 与 source/raw-ref invariants。其 real-data report schema v3
SHA-256 为 `dfdafe870fdaa0c883a27e0eddacfe374f2ec661366ff50b53bbe66a13a33a24`。
真实 Host 只发送一个 turn，捕获了 exact live turn id；444 组 current/recent HTTP 响应均为 200。
orderly stop/reopen 后 current/recent bytes 完全相同，exact Undo 恢复原六回合 projection，最终再次 strict validate
为 149 selected events、Idle、同一 setup-only suffix head。外置日志恰好 1 agent、0 maintenance，session repo 内
没有 call-log 目录。

Clone B evidence 位于
`gitignore/session-journal/reviews/2026-08-02-production-readiness/r4-final/clone-b/`；汇总文件 SHA-256 为
`92ad1b86936a6a0326d91ee6499079e48e4cd3bc704b1fced4cd21d5dc7f9e20`。汇总中的 evidence paths 描述原始
`run-b` 布局；content-free 归档副本规范化为 `tests/` 与 `reports/`，provider call logs 和普通运行日志未复制。

## 6. Final Beta decision

**GO**。两个 fresh clone 合计执行 2080 passed、10 expected skips、0 failed；两次真实数据与 provider workflow
均保持 raw/source authority，第二次还直接观测到 live turn id 并证明 Host 并发 read surface 无 500。
没有未处理 P0/P1；P2 已修复或明确接受；所有修复与最终文档均经过实施者之外的 reviewer 复核。

GO 只表示：本文快照中的首个 Beta support surface 与 A/B/C wire 值得开始一次有监控、可停机、
可备份的首次生产运行；不把 accepted/deferred 边界提升为承诺。
