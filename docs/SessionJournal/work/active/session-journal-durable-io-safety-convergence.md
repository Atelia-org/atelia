# SessionJournal DurableIO 安全收敛方案（S4-D）

状态：Proposed；设计核验已完成，implementation 尚未开始  
审阅基线：`32683f4c7baeeca82f33a423911c92d52778d8f7`  
撰写日期：2026-08-16

## 1. 结论

外部建议把当前 durable 层描述为四套
`Paths / Files / AcquireShared / AcquireExclusive / EnsureSlots / ReadBounded /
WriteAtomic / FlushDirectory` 样板，并进一步建议统一
`Open / Create / Inspect / Backup / Export` 的 result family。

当前代码核验后的裁决是：

1. **原 S4 整体 No-Go。** durable implementation 与 public result contract 没有共同
   authority owner、依赖顺序或验收门槛，不应绑成一个实施包。
2. **S4-D 条件成立。** 四个 durable owner 确实重复了少量 Linux syscall、lock 与 directory
   flush 机制；但不存在四套完整、行为等价的 durable layer。只有先锁定 threat/settlement
   矩阵，且证明至少两个 owner 能复用同一 hardened kernel，才允许抽取。
3. **S4-R 当前 Reject / Retain。** result family 的叶子名称相似，但合法 case 集、payload
   authority 与 operator action 不同。跨 owner generic/sum 会增加不可达或不可恢复的可表示状态，
   不是语义化简。
4. 当前默认保持 owner-local。本文批准的是 **D0/D1 调查与校准路径**，不是预先批准新的
   shared assembly，也不是要求最终一定产生代码变更。

本方案不改变 canonical bytes、SQLite schema、durable path/layout、lock topology、wire reader
接受语言或既有 typed result。若后续接受更严格的 path/hostile-writer contract，应另记为显式
security hardening，不伪装成机械去重。

## 2. 当前事实：四个 owner 不是四份同一实现

当前相关实现约 2,002 行，分属 T、C、G-Control、G-Store：

| Owner | Path/layout | Lock 与 slot | Read / publish | 关键差异 |
|:--|:--|:--|:--|:--|
| T · HistoryTimeline | per-Ref lock、locator、per-Timeline SQLite | shared/exclusive；create 折在 exclusive acquire | caller cap 的 bounded read；create-new/replace byte publish；path fsync | managed path/reparse 检查；min 1；Timeline 自有错误映射 |
| C · Cadence | per-Ref cadence directory、state、1 lock | held directory fd；shared/exclusive；0600 slot | fd-relative bounded read；`renameat2(RENAME_NOREPLACE)` / `renameat`；held-fd fsync | `openat/O_NOFOLLOW/fstat`、inode/device/uid/mode、0700/0600、canonical directory identity |
| G · Control | per-Ref/per-Timeline whole-state、lifetime + writer 双 lock | shared/exclusive lifetime + exclusive writer | fixed cap/min 2；whole-state replace；post-publish failure settlement | path-based；两个 lock slot；Control-specific indeterminate 与 cleanup |
| G · Store | whole-grid SQLite、lifetime lock、SQLite sidecars | shared/exclusive lifetime；1 lock | 没有通用 bounded canonical read/`WriteAtomic`；SQLite create/reset 自有 publication | physical witness、SQLite busy/schema、reset/backup 边界 |

关键代码：

- T paths/files：[HistoryTimelineStorage.cs](../../../../prototypes/SessionJournal.HistoryTimeline/HistoryTimelineStorage.cs)
  `:34-499`；
- C paths/files：[CadenceDurability.cs](../../../../prototypes/SessionJournal.RecapGrid.Cadence/CadenceDurability.cs)
  `:7-630`；
- Control paths/files：[ControlDurability.cs](../../../../prototypes/SessionJournal.RecapGrid/Control/ControlDurability.cs)
  `:7-358`；
- Store paths/files：[StoreDurability.cs](../../../../prototypes/SessionJournal.RecapGrid/Store/StoreDurability.cs)
  `:49-324`，SQLite publish 另见
  [StoreRuntime.cs](../../../../prototypes/SessionJournal.RecapGrid/Store/StoreRuntime.cs)
  `:34-81` 与
  [StoreMaintenance.cs](../../../../prototypes/SessionJournal.RecapGrid/Store/StoreMaintenance.cs)
  `:205-275`。

### 2.1 已确认的窄重复

目前只有两块接近 exact-equivalent：

- 四者最终都以 `flock(LOCK_SH/LOCK_EX | LOCK_NB)` 获取进程间 lease，并把 Linux
  `EWOULDBLOCK` 映射为 owner-specific Busy；
- T、Control、Store 都按 path 执行
  `open(O_RDONLY|O_DIRECTORY|O_CLOEXEC) -> fsync -> close`。

即使在这两块，外围的 platform gate、shape validation、SafeHandle ownership、errno/error-code
映射仍不同。Cadence 对已持有且验证过的 directory fd 执行 fsync，不能降级为 path reopen。

### 2.2 必须保持 owner-local 的策略

以下差异不是可随手参数化掉的样板：

- durable path/layout 与 lock 数量；
- repository root、ancestor、reparse/symlink 的接受边界；
- `FileMode`、access/share/create-new 规则与 existing-slot shape/mode 校验；
- bounded read 的 min/max、share mode 与 growth detection；
- create-new 与 replace 的 publish primitive；
- Before/AfterPublish 相对 rename、directory fsync 的精确时序；
- temporary path 被重新占用后的 cleanup 策略；
- `Busy`、`Invalid`、`PlatformUnsupported`、`CommitIndeterminate` / `PublishIndeterminate`
  的 owner-specific 映射；
- Store 的 SQLite lifetime、physical witness、WAL/SHM 与 reset 语义。

不得用 `DurableFilesOptions` 的大量 flags/callbacks 隐藏这些合同。若抽象需要为每个 owner
重新组合一套策略，它没有减少独立语义。

## 3. Result family 裁决：保留 operation-specific closed vocabulary

当前 HEAD 有 67 个 public abstract `*Result` family（T 25、C 9、G 30、H 3）。数量大是审阅
线索，不是合并证明。

### 3.1 只有 Open/ReaderOpen 成对近同构

| Owner | Open / ReaderOpen 的 exact case set | 结论 |
|:--|:--|:--|
| T | `Opened / Absent / Busy / UnsupportedSchema / Invalid` | 仅 Handle 类型不同 |
| C | 上述 + `PlatformUnsupported` | 仅 owner-local 成对近同构 |
| Store | 与 C 同形 | 与 C 不共享 assembly/authority |
| Control | `Opened / Absent / TimelineAbsent / TimelineUnsupportedSchema / Busy / UnsupportedSchema / Invalid` | 必须保留 upstream Timeline provenance |

把它们改为 `OwnerOpenResult<THandle>` 仍会：

- 改动至少 52 个 current `.cs` consumer 文件的返回签名与 pattern syntax；
- 允许 caller 构造任意 `THandle` 的 generic result universe，而不是只允许当前两种 handle；
- 只减少声明数量，不减少合法 workflow、authority path 或 operator action；
- 无法自然跨 T/C/G 承载：下沉到 SessionJournal 会污染 raw owner，新增 public contract
  assembly 又会扩大 API/topology。

因此连这组最接近的候选也不进入 implementation。

### 3.2 Create/Inspect/Export/Backup 不是同一代数

- `Absent` 在 Open/Inspect 中是合法状态，在 Create 中通常不是；Control 还必须区分自身 absent
  与 Timeline absent。
- `UnsupportedSchema` 必须指出 Timeline、Cadence、Control 或 Store owner；不能折成一个无来源
  的 `int Version`。
- `StaleTimelineHead`、`StaleControlHead`、Cadence head stale 与 Store physical-witness
  `StaleConfirmation` 分别要求重新读取不同 authority。
- `CommitIndeterminate(Intended, Observed)` 的 payload 是具体 owner 的 durable identity；Control
  operation 还携带 `OperationKey`。Backup destination publication 则特意使用
  `PublishIndeterminate`。
- Store `Verify.Unhealthy(errors, incomplete)` 是数据健康状态，不是通用 `Invalid`。

代表性 contract：

- [HistoryTimelinePersistenceContracts.cs](../../../../prototypes/SessionJournal.HistoryTimeline/HistoryTimelinePersistenceContracts.cs)
  `:98-162,481-565`；
- [CadenceContracts.cs](../../../../prototypes/SessionJournal.RecapGrid.Cadence/CadenceContracts.cs)
  `:148-235`；
- [ControlContracts.cs](../../../../prototypes/SessionJournal.RecapGrid/Control/ControlContracts.cs)
  `:616-779,830-905`；
- [StoreContracts.cs](../../../../prototypes/SessionJournal.RecapGrid/Store/StoreContracts.cs)
  `:294-420`。

禁止用以下方式制造表面统一：

1. 包含所有 case 的跨 owner superset union；
2. `Kind` enum + nullable payload bag；
3. `AteliaResult<T>` / string error code 替代 typed operator action；
4. marker interface 或 shared base，但仍保留原 family；
5. `Never`/多 generic 参数/runtime validator 排除不可能状态；
6. 为保持旧调用语法而增加 compatibility wrapper。

未来只有当单一 owner 内的候选能证明 **exact case set、payload authority、operator action 与
consumer workflow 全部相同**，才另立 per-owner direct-cut 方案；不得建立通用 result framework。

## 4. S4-D 抽取资格与承载位置

### 4.1 Go gate

只有同时满足下列条件，才进入 shared kernel prototype：

- 至少两个 owner 需要同一 primitive，且 pre/post condition、errno、handle ownership、path/shape
  要求和 crash settlement 已逐项证明等价；
- 抽取后 owner adapter 仍显式拥有 paths、schema/caps、error codes、test hooks 与 public result；
- 不用 flags/callback matrix 重新编码四套 owner policy；
- canonical bytes、path/layout、lock topology、reader acceptance 与 public API inventory 不变；
- 不扩大 `SessionJournal` 或 T internals 的 production caller set；
- RG0001/RG0002 与 WalkingSkeleton 能识别并限制新的依赖；
- 至少两个 owner 完成迁移后，重复和分叉点有净减少。

任一条件不满足，合法结论是 **Retain owner-local**。

### 4.2 承载选项

| 选项 | 结论 | 原因 |
|:--|:--|:--|
| 放 S/T/C/G/H/O 现有 owner | Reject | 依赖方向、语义 ownership 或 production IVT privilege 不成立 |
| 放 `Atelia.Primitives` | Reject | Linux-specific durable IO 会污染跨域基础 result/value 层 |
| linked source / MSBuild `Compile Link` | Reject | 同一实现复制进多 DLL，且会形成 RG0001/RG0002 source-owner 旁路 |
| G 内 `Durability/` internal module | Conditional | 只适用于 Control+Store 的 exact common subset；必须扩展 RG owner/allow matrix；当前仅为两处 raw syscall 时收益过小 |
| 新 `src/DurableIO` / `Atelia.DurableIO` | Conditional | 只有跨 assembly 至少两个 owner 收敛到同一 fd-relative kernel 时成立 |

若 D2 选择 `Atelia.DurableIO`，它必须：

- 只依赖 BCL，反向零引用 S/T/C/G/H/O；
- 只拥有 SafeHandle、Linux syscall、fd-relative shape/identity 与低层 failure stage/errno；
- zero exported types；以 exact IVT 只授予 T、C、G 和自身测试；
- 不拥有 SessionJournal/Timeline/Control/Store paths、limits、error code、result、backup/reset policy
  或 owner hooks；
- 明确把物理输出描述为“5 个 product owner assemblies + 1 neutral infrastructure”，不得把
  原 11→5 实施记录悄悄改写为仍只有 5 个 DLL。

若 D2 只选择 G-local `Durability/`，必须把它作为 RG0001/RG0002 的显式 source/target owner，
只允许 Control、Store 使用；Manager、Runtime、Getter、Online、AgentControl 的引用 mutation
必须报错。

## 5. 工作包

### D0 — Durability fact / threat lock（只读）

产出逐 owner matrix：

- absolute/relative path 与 canonical root；
- directory/regular-file/symlink/FIFO/device/permission/identity 规则；
- slot creation、lock mode、FileAccess/FileShare；
- bounded read min/max/growth；
- temporary create、rename mode、cleanup；
- publish point、directory fsync、indeterminate settlement；
- platform gate、exception/error code 与 operator action。

明确 hostile concurrent same-directory writer、ancestor swap、repository symlink 与 permission policy
是 accepted risk 还是 target protection。D0 不得把不同现状先归一成抽象输入。

**停止门：** 找不到至少两个 exact-equivalent consumers，关闭 S4-D，记录 owner-local retain。

### D1 — Owner-local safety calibration

先补 mutation/negative evidence，不移动实现：

- ancestor/directory swap；
- symlink、FIFO、device-shaped slot；
- temporary name reoccupation 与 orphan policy；
- create-new no-replace；
- shared/exclusive Busy；
- read growth/change；
- rename 后 fsync failure 对应 exact typed indeterminate；
- owner/mode policy；
- process crash 后只允许 old-or-new valid state。

若校准暴露 owner bug，先在 owning assembly 单独修复并 review；不得借抽象同步改变四个 contract。

**重新判定门：** 所有 owner-local 修复及独立 review 完成后，必须重新生成 D0 matrix。任一候选
consumer 的 pre/post condition、failure stage、handle ownership 或 settlement 不再
exact-equivalent，立即以 Retain 结束，不得沿用修复前的 Go 结论。

### D2 — Conditional shared-kernel prototype

仅在 D0/D1 Go 后执行：

1. 选择 G-local `Durability/` 或 neutral `Atelia.DurableIO`，不得两者并存；
2. 先抽最小 syscall/fd kernel，不抽 `Paths` base class、schema、limits 或 public result；
3. 先迁一个 reference owner，再迁第二个 consumer 证明 seam；
4. owner wrapper 保持原 error message/code、Busy 映射、publish stage 与 handle ownership；
5. 若第二个 consumer 需要大量 special cases，删除 prototype 并回到 owner-local，而不是扩张 framework。

### D3 — 逐 owner 迁移

一个 owner 一个提交与独立 review。迁移顺序由 D0 的 exact common subset 决定，不预设把 Cadence
整套复制给其他 owner。

每包都必须先保存前一候选的 behavior/API evidence；禁止同时迁另一个 owner、改 public result 或
改 durable layout。Store 只迁被证明等价的 lock/directory subset，SQLite open/WAL/witness/reset
策略继续 owner-local。

**逐包回退门：** 任一 owner 出现 public API、canonical bytes、path/layout、lock topology、
reader acceptance 或 crash settlement delta，回退该 owner 包并停止后续迁移；不能靠更新 baseline
或放宽测试把 delta 接纳进“共同 kernel”。

### D4 — Closure

- 删除所有已被证明完全替代的 duplicate syscall wrapper；不以删除 owner adapter 为目标；
- 更新 exact project graph、IVT、RG gate、WalkingSkeleton 与 public surface gate；
- 保存新 candidate 的 API/canonical/crash evidence；
- 只有实现确实落地后才更新 current architecture map 与 11→5 successor/caveat；
- 将本文归档，并在 `evidence/` 记录 exact commit 与验证结果。

## 6. 验收矩阵

### 6.1 必跑 focused gates

| Owner / boundary | Focused evidence |
|:--|:--|
| T | `HistoryTimelineCrashRecoveryTests`；`HistoryTimelineDurableLedgerTests`；T regular + PublicSurface |
| C | `CadenceTests` 的 absent/no-create、Busy、reoccupation、symlink/FIFO/device、permission、settlement；C regular + PublicSurface |
| Control | `ControlSettlementTests`、`ControlVerticalTests` lifetime lock、`ControlCrashRecoveryTests`；Control regular + PublicSurface |
| Store | `StoreMaintenanceAndFailureTests` witness/reoccupation/symlink/Busy、`StoreCrashRecoveryTests`；Store regular + PublicSurface |
| Topology | RG0001/RG0002 focused mutations、WalkingSkeleton、T/C/G/H/O 及新增 D exact reference/export/IVT gate |
| Consumers | Manager、Getter、Online、AgentControl、Hosting、CLI、Galatea/Galatea.Server focused tests |

CrashHarness 必须在 isolated checkout 以标准 repo-local `bin/...` 布局构建；现有测试会按 sibling
路径定位 harness，不应通过共享工作区并行 build 校准。

### 6.2 Candidate closure

最终候选必须满足：

- full solution build/test 串行通过，使用 `-m:1 -nr:false`；
- clean/fresh output 中 project graph、DLL、IVT 与 exported types 精确符合方案；
- G public API inventory byte-equivalent；T/C owner surface reflection 无 delta；
- canonical codecs/goldens、SQLite schema/resource 与 durable path inventory 无 delta；
- crash/reopen 仍只产生 absent/old/new 中允许的完整状态；
- result-to-operator-action matrix byte-for-byte/逐 case 无变化；
- `python3 scripts/check_session_journal_docs.py` 为 0 diagnostics；
- independent reviewer 对每个 owner 包和最终跨包缝均无 blocker。

## 7. 明确非目标

1. 合并或重命名 public result family；
2. 用 exceptions/string codes 替代 expected typed outcomes；
3. 统一四个 owner 的 path/layout、lock 数量、schema 或 reset/backup policy；
4. 把 SQLite I/O 包装成通用 atomic-file API；
5. 通过新增 public API 或扩大 `SessionJournal/T -> G` IVT 获得复用；
6. source generator、reflection、DI 或 options framework；
7. 跨平台 durability 承诺或真实断电认证；
8. 顺手修复与本轮 exact-equivalence 无关的 owner 行为。

## 8. 完成定义

本方案有两个同样合法的完成态：

- **Retain completion：** D0/D1 证明没有至少两个 exact-equivalent consumers，记录矩阵与风险，
  不产生 shared layer；
- **Extraction completion：** D2-D4 证明一个窄 kernel 被至少两个 owner 使用，owner-specific
  durability/result contract 不变，重复实现被删除且所有 gates 通过。

“新增了 shared helper，但只有一个 consumer”“保留 owner-local wrapper 同时继续复制 syscall”或
“为了统一而让 result/path policy 变宽”均不算完成。
