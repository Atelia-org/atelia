# Galatea G2A：Repeatable staging acceptance runbook

> **状态**：G2A operator runbook  
> **日期**：2026-08-01  
> **上位计划**：
> [Galatea → SessionJournal + DerivedRecap cutover plan](galatea-session-journal-cutover-plan.md)

## 1. 目标与边界

本runbook用于从同一份legacy upgrade export反复构建、验证和试跑Galatea的
SessionJournal staging。它生成两类repo，权限边界不同：

- **staging repo**：允许写repo-owned planner config与DerivedRecap sidecar，禁止写任何新的raw
  SessionJournal event；
- **acceptance clone**：从已经通过验证的staging完整复制而来，只供scripted或real Host canary写
  raw event，用完即丢弃，绝不提升为production repo。

production CLI仍是import、validate、Recap planning与materialization的唯一实现。本runbook只编排
这些命令和检查content-free evidence，不复制importer、Planner、Store或Host状态机。

以下行为一律禁止：

- 原地修改或覆盖legacy ChatSession repo、现有Galatea `sessionDir`或以前的staging；
- 对staging使用`--force`、`recap reset`，或在provider失败后自动reimport；
- 把connections secret、request/response正文、Recap正文或其payload hash抄入汇总报告；
- 把任何运行过canary的clone作为G2B activation repo。

## 2. Run root与信任区

每轮验收使用一个新的、显式命名且尚不存在的run root。推荐布局：

```text
gitignore/galatea-g2a/<run-id>/
  staging-repo/              # raw只读；config/DerivedRecap可写
  acceptance-clones/
    scripted-<id>/           # raw可写
    real-<id>/               # raw可写
  reports/                   # content-free JSON与命令stdout/stderr
  call-logs/                 # 可能含正文，限制访问且不进入repo
  host-config/               # acceptance-only config；不保存resolved secret
```

开始前把下列路径全部规范化为绝对路径，并拒绝symlink/reparse ancestor：

1. source export；
2. 本轮run root、staging repo和每个acceptance clone；
3. 当前实际运行的Galatea config中**所有**用户的`sessionDir`。

run root、staging及clone必须与每个active `sessionDir`既不相同、也互不构成ancestor/descendant；
source export也不得位于任何输出目录内。若无法确认live Server实际使用哪份config，停止，不猜测。

目标export的当前校准事实是：

```text
bytes       1,281,881
SHA-256     b71822a27003e8d9f9b9c0ff956ca7c268267aba72221be89df154ed7d4751f3
```

这些值只识别source export，不是Recap payload hash。export不匹配时应先重新校准和审阅导入事实，
不能通过修改验收期望继续执行。

## 3. Fresh staging provisioning

以下示例假设调用者已经完成第2节的路径检查。变量都应是规范化的绝对路径：

```bash
source_export=<absolute-legacy-upgrade-export.json>
run_root=<absolute-new-run-root>
staging_repo="$run_root/staging-repo"
reports="$run_root/reports"
call_logs="$run_root/call-logs"
connections=<absolute-connections.json>

mkdir -p "$reports" "$call_logs"
```

`run_root`必须在本轮开始前不存在；`staging_repo`也必须不存在。不要在命令中添加`--force`。

先记录source的bytes与SHA-256，再用production importer构建fresh repo：

```bash
stat -c '%s' "$source_export" > "$reports/source-before.bytes"
sha256sum "$source_export" > "$reports/source-before.sha256"

dotnet run --project prototypes/SessionJournal.Cli -- \
  import-legacy-json \
  --input "$source_export" \
  --output "$staging_repo" \
  --report-json "$reports/import.json"

dotnet run --project prototypes/SessionJournal.Cli -- \
  validate \
  --input "$staging_repo" \
  --branch main \
  --report-json "$reports/validate-imported.json"
```

必须检查：

- import report schema是`atelia.session-journal.legacy-import-report.v1`；
- `observationCount=71`、`agentActionCount=71`、`skippedCompactionCount=2`、`skippedRecapCount=2`、
  `warnings=[]`；
- validate得到148个events且phase为`Idle`；
- source schema、branch与final head均存在且符合本轮校准记录。

随后初始化repo-owned config和Store：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap planner-config init \
  --input "$staging_repo" \
  --report-json "$reports/planner-config-init.json"

dotnet run --project prototypes/SessionJournal.Cli -- \
  recap planner-config inspect \
  --input "$staging_repo" \
  --report-json "$reports/planner-config-inspect.json"

dotnet run --project prototypes/SessionJournal.Cli -- \
  recap create \
  --input "$staging_repo" \
  --branch main \
  --report-json "$reports/recap-create.json"
```

以`planner-config inspect`的repo snapshot为证据，记录config SHA、estimator id、实际R/B、active
profile/target/Maintainer identity与limits。不要从进程default反推repo配置。

## 4. Raw不变式

import完成后、运行任何Recap命令前，捕获：

- `validate`报告中的exact raw head、event count与phase；
- `events/`和`refs/`内“相对路径 + 文件bytes”的稳定tree fingerprint。

fingerprint不得包含DerivedRecap、planner config、lock file、mtime或其他filesystem metadata。一个可用的
GNU userland实现是：

```bash
(
  cd "$staging_repo"
  find events refs -type f -print0 \
    | sort -z \
    | xargs -0 sha256sum
) | sha256sum > "$reports/raw-before.sha256"
```

第5节完成后重新运行`validate`并用同一算法生成`raw-after.sha256`。exact head、events/refs
fingerprint、event count和`Idle` phase必须逐项不变。planner config、Store lock、Building、Published
等sidecar变化不属于raw mutation。

## 5. `dsv4p` Recap run与恢复

connection必须显式写成`dsv4p`，不能依赖connections文件未来可能变化的default：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap run \
  --input "$staging_repo" \
  --branch main \
  --connections "$connections" \
  --connection dsv4p \
  --call-log-dir "$call_logs/recap-attempt-001" \
  --report-json "$reports/recap-attempt-001.json" \
  >"$reports/recap-attempt-001.stdout" \
  2>"$reports/recap-attempt-001.stderr"
```

每次尝试使用新的attempt编号、call-log目录和report文件。调用次数必须受repo config的hard limits和
本轮验收预算约束；禁止无界循环。

结果处理：

- `Published`：进入materialization检查；
- `BlockFailed`或进程/网络失败：保留staging、Building、已有final block、report与call log，停止本次
  attempt；
- retry时对**同一个staging repo**再次执行`recap run`，使用新的attempt evidence。Building-first
  preparer会服从frozen manifest并只补缺失/损坏部分；
- 不运行`reset`、不删除Building、不reimport，也不因provider失败创建dual state。

Published后，对strict latest Published（ordinal 0）做materialization inspection：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap materialize-inspect \
  --input "$staging_repo" \
  --branch main \
  --nth-previous 0 \
  --report-json "$reports/materialize.json"
```

报告必须为`Selected`，包含两个合法、canonical ordered contributions，分别对应
`world-understanding`和`autobiographical` target；不得把正文或payload hash复制到G2A汇总证据。

随后立即再运行一次`recap run`，使用新的空call-log目录和report：

```bash
dotnet run --project prototypes/SessionJournal.Cli -- \
  recap run \
  --input "$staging_repo" \
  --branch main \
  --connections "$connections" \
  --connection dsv4p \
  --call-log-dir "$call_logs/recap-no-build" \
  --report-json "$reports/recap-no-build.json"
```

结果必须为`NoBuild`且provider call count为0。最后完成第4节的raw-after检查，并再次计算source
bytes/SHA-256；source before/after必须相同。

真实LLM生成的block content、payload SHA与publication envelope SHA只在本轮Store commitment/self-
check内有意义，不作为跨run golden。跨run可固定的是raw head、config语义、admission/absorption
addresses、block ids/targets/modes与Published/NoBuild shape。

## 6. Acceptance clone与Host gate

只有第3～5节全部通过后才能复制staging。复制时CLI/Host必须已经退出，destination必须不存在：

```bash
clone="$run_root/acceptance-clones/real-001"
mkdir -p "$(dirname "$clone")"
cp --archive --reflink=auto -- "$staging_repo" "$clone"
```

scripted Host tests与real Host canary分别使用不同clone，不共享raw writer。external
real-staging suite直接覆盖：

- 打开既有repo并展示newest-first最近6轮；
- scripted fresh turn、dispose/reopen及exact Undo；
- 已Published sidecar的exact selection/materialization前后recent snapshot、rewind token与raw head不变；
- stale rewind token不能撤销更新后的turn；Undo回到setup-only suffix时仍可显示较早turn，但token为
  `null`；
- connection切换只影响新turn。

以下recovery规则不在external suite中复制test-only failpoint harness，而由组合证据继续作为G2A gate：

- G1 deterministic Host tests：Prepared safe resume、Started默认Refuse/显式restart、durable original
  connection exact binding；
- CLI real-data acceptance：含Published Recap的Prepared canonical request在删除sidecar后仍可恢复；
- 本轮Galatea logging tests：logging wrapper不改变completion target identity，Prepared exact recovery在
  启用logging后仍成立。

任何一层证据失败都不能用另一层替代；这里的组合只避免重复另一套failpoint authority，不把它们描述
成external real-staging suite的直接覆盖。

已provision且Published的staging通过opt-in external Fact进入scripted Host gate；测试本身为每个会写
raw的case创建独立临时clone：

```bash
ATELIA_GALATEA_G2A_STAGING_REPO="$staging_repo" \
dotnet test tests/Galatea.Server.Tests/Galatea.Server.Tests.csproj \
  -m:1 -nr:false --no-restore \
  --filter 'FullyQualifiedName~GalateaG2AStagingHostAcceptanceTests'
```

未设置该环境变量时测试应明确skip，而不是用空repo冒充external acceptance。test output保存在
run-root reports目录或由CI作为独立evidence保留。

real Host使用run-root下的acceptance-only config：`sessionDir`必须是clone绝对路径，listen URL只绑定
`127.0.0.1`，并使用一次性本地账号。它的sibling `connections.json`只保存non-secret connection
配置和环境变量名；resolved secret不得写入run root。通过`Galatea__ConfigPath`显式选择该config，
不要修改现役Galatea config。config顶层`callLogDir`显式指向run-root下的独立
`call-logs/host-real-001`；相对路径以config目录为基准，且Host会拒绝它与任何`sessionDir`相同或互相
嵌套，也会在创建client/日志目录之前拒绝二者existing path chain中的symlink/reparse point。

real-provider canary只允许：

1. read-only current/recent检查；
2. 一次显式选择`dsv4p`的fresh turn；
3. 成功后reopen并exact Undo该turn。

canary message应超过280字符或4行，使现有Galatea preprocessor按policy跳过可选normalizer，从而避免
引入第二次非目标provider call。Host stdout/stderr放在`reports/`；agent与Maintainer request call log
由上述`callLogDir`写到repo外。验收后检查相应call count符合本轮预算，且session repo内没有call-log
目录。

canary失败时停止并保留clone及durable tail：

- 不自动Undo、abandon、reset或切换connection；
- Prepared按safe resume处理；Started保持uncertain并默认Refuse；
- 若需要从pristine状态重试，从已验证staging创建一个全新的clone。

无论成功或失败，real clone都已经越过“staging raw只读”边界，不能回填staging，更不能用于G2B。

## 7. Content-free evidence与完成判定

所有CLI JSON、stdout/stderr和测试结果保存在run-root。额外生成一份content-free summary，至少引用：

- source before/after bytes与SHA-256及是否相同；
- git commit、CLI/Host build identity和run id；
- import counts、warnings count、validate phase、raw exact head与raw tree fingerprint；
- planner config SHA、estimator、R/B、profiles/targets/limits；
- 每次Recap attempt的result、call count及Building-first resume关系；
- Published admission/absorption addresses、block ids/targets/modes、materialization contribution
  count/length与第二次`NoBuild`；
- scripted/real clone identity、canary的前后head/phase、reopen/Undo结果；
- 每项gate的`Passed / Failed / NotRun`，不得把provider失败改写为成功。

summary不内联source正文、对话、system prompt、Completion request/response、Recap正文、credentials或
payload hash。详细call log可能含正文，只保留在受限目录，并在summary中记录其相对位置和数量。

只有offline/deterministic gate、bounded `dsv4p` Recap gate、scripted Host gate和real Host canary都通过，
本轮G2A才是`Passed`。外部provider故障不推翻已通过的offline证据，但整轮状态仍必须是`Failed`或
`NotRun`，等待有界重试。

## 8. 与G2B的边界

G2A的任何staging或clone都不是activation candidate。G2B必须在旧Server quiesced后：

1. 捕获legacy exact head并重新生成final export；
2. 从final export构建一个全新的activation repo；
3. 重新执行import、validate、config/Store provisioning、Recap run/materialize与source/raw invariant
   检查；
4. 确认该repo从未运行scripted或real agent canary，才允许按exact-head activation步骤切换。

因此G2A完成后可以保留run-root用于审计，也可以由operator显式清理；runbook本身永不自动删除失败
现场，也不提供“promote staging/clone”操作。
