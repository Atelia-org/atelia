# RecapGrid 前置输入简化：设计与实施记录

> 状态：历史前置切片已实施并通过本地验证；代码提交 `258125fd`，验证结果见 §8；尚未部署。当前 Store v3 实施将删除本切片保留的 digest 入口，不能把下文历史验证当作新格式证据。
> 日期：2026-09-14；规划源码基线：`f53ac2e2`；实施基线：`606f3c04`。
> 承接[身份简化总设计](identity-simplification-design.md)。本文 §1–5、§7–8 保留 `258125fd` 当时的无格式变更合同与验证；后续已按用户新决定改为清旧 Recap、全部重构后统一重建，见 §6。

## 1. 最小目标

**前置摘要只传 `PreviousCells`，不再另传可以从它重算的 `PriorInputProjection` 对象。**

```text
PreviousView + PreviousCells     // 前驱成员关系与实际内容
Spec.PriorInput                  // FirstRow，或已有持久 projection digest

Runtime：校验前驱成员 → 从实际 cells 计算原 digest → 对照 Spec
```

删除两个瞬态公开模型及其完整 wire 表示，保留现有 digest 算法和值。这个切片不改 Timeline、Control、Store、EvaluationKey 或 Prepared 格式，不需要迁移数据。

这是当时可独立交付的无格式变更步骤，**不算完成总设计 §6.1/§6.3，也不减少持久 hash 数量**。比只删无人调用的 decoder 更有价值：一起消除 Manager → Runtime 的重复输入与对账；又不要求先确定普通结果 ID 和全图转换算法。

## 2. 需求台账与证据

| 要求 | 来源 |
|---|---|
| 个人未发布项目，及时重构，减少无价值 hash/复杂性；无防恶意篡改需求 | 用户与根 AGENTS.md |
| 已有会话、摘要、first-winner、回执与冻结请求须保持 | 用户已采纳总设计；Store/Manager/SessionJournal 的当前消费者 |
| 摘要必须使用正确前驱、列顺序和实际内容；同内容不同来源仍复用缓存 | ManagerRowBuild、RuntimePreflight、Getter provenance 与现有测试 |
| 保留逐行推进、未完成 cell 的 missing-only 恢复、既有预算和原子发布 | 当前 Manager/Store/Online；本切片不调整调度 |
| 先规划审查，再按批准方案实施、验证和提交 | 用户先要求规划，随后明确批准本切片实施；过去 E2E 不构成本轮部署证据 |

故障模型仍是本地进程崩溃、重启、正常重试与并发构建。本切片只改变内存中的派生表示，不引入新的持久状态、身份注册表、兼容层或迁移器。

规划基线源码证据（描述删除前的对象与调用链）：

| 位置 | 当前事实 |
|---|---|
| [BuildContracts](../../prototypes/SessionJournal.RecapGrid/Abstractions/BuildContracts.cs) `PriorInputProjection` | 私有构造；Create 生成只读内容列表、digest 和完整 canonical 缓存；Decode 最终仍重走 Create |
| 同文件 `PriorProjectedContent` | 仅包含列名和 ContentDigest，均可由现有不可变 cell 取得 |
| [ManagerRowBuild](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerRowBuild.cs) `DerivedRowPlan/DeriveRowPlan` | 从 PreviousCells 构造 projection，同时把它的 digest 写入 Spec.PriorInput |
| [ManagerContracts](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerContracts.cs) `FrozenRowBatch`、[ManagerWavefront](../../prototypes/SessionJournal.RecapGrid/Manager/ManagerWavefront.cs) | 继续同时传 PreviousCells、Spec 和完整 projection 对象 |
| [RuntimePreflight](../../prototypes/SessionJournal.RecapGrid/Runtime/RuntimePreflight.cs) `ValidatePrior` | 先校验前驱成员，再重建 projection，对照 Spec、batch 的 digest，最后重复比 canonical bytes |
| [Getter materializer](../../prototypes/SessionJournal.RecapGrid/Getter/RecapGridContextMaterializer.cs) `EvaluatePrior` | 临时构造 projection，仅使用其 digest 判断前驱输入对齐 |
| [CanonicalContractTests](../../tests/SessionJournal.RecapGrid.Abstractions.Tests/CanonicalContractTests.cs) | 全仓唯一 projection decoder 调用；保留着完整 wrapper roundtrip/golden 测试 |

全仓搜索未发现外部生产消费者读取 `OrderedContent`，也未发现 projection wrapper 的持久读取者。`ToCanonicalBytes` 有一个真实运行消费者，即 Runtime 的重复对账，不能把它误写成完全无人调用。

## 3. 目标接口与删除范围

在既有 `PriorInputProjectionDigest` 上增加一个计算入口：

```csharp
public static PriorInputProjectionDigest FromCells(
    IReadOnlyList<RecapCellArtifact> orderedCells);
```

它按顺序读取 cell 的 `LogicalColumnId` 和 `ContentDigest`，用原内部 body 编码计算原 digest。保留已有值构造器供读取持久 digest；计算与读取并不是两个运行模型。

必须保持以下 body 和 domain 不变：

```text
domain = atelia.recap-grid.prior-projection.v1
body   = {schemaVersion:1, orderedContent:[{logicalColumnId, contentDigest}, ...]}
```

继续使用现有 canonical 编码选项、字段顺序和 domain hash 函数。不得改成 hash 原文、cell ID、定义 ID 或前驱 RowView ID；否则会改变已有 EvaluationKey 和缓存命中。

| 删除 | 替代方式 |
|---|---|
| `PriorInputProjection`、`PriorProjectedContent` 两个公开类 | 从实际 cells 直接取得 digest，不增加新的公开投影模型 |
| `FrozenRowBatch.PriorProjection` 与其构造参数 | batch 已有 PreviousView、PreviousCells 和 Spec |
| `DerivedRowPlan.Projection`、ManagerWavefront 中的转运 | 只把计算出的 digest 放入既有 Spec.PriorInput |
| projection 的 OrderedContent 属性、Create/Decode/ToCanonicalBytes、私有 decoder、完整 bytes 缓存 | 统一计算入口；没有新的 wrapper reader |
| `PriorInputProjectionDto` 完整 wrapper DTO | 原 `PriorInputProjectionBodyDto` / `PriorProjectedContentDto` 仅供内部 hash 编码，暂保留 |
| Runtime 对 batch projection 的 null/digest/canonical 重复对账 | 校验实际 cells 与独立的 Spec expected digest |

Getter 改用同一计算入口。不要在 Manager、Runtime、Getter 中复制三份 hash 编码，也不要留下空 projection、常量 digest 或过渡 adapter 来满足旧构造参数。

以下名称相似但仍有消费者，保持：`PriorInputReference.FirstRow/Projection`、`PriorInputProjectionDigest`、`ContentDigest`、`EvaluationKey` 的持久布局；Runtime/Hosting 的 `PriorProjectionDigest` 证据字段；`RuntimeRenderer.PriorProjectionSchemaId` 与 Family 的模型输入协议。工具 catalog、runtime identity、模型 prompt 和既有 schema 均不 bump。

## 4. 不可删除的校验

1. **FirstRow。** 仍要求 PreviousView 为 null、PreviousCells 为空。有前驱的空 projection 不自动等于 FirstRow；由 Spec 的 discriminant 区分。
2. **前驱成员。** Projection 必须有 PreviousView；cell 数与成员数一致。逐 ordinal 比较 LogicalColumnId、DefinitionDigest、CellDigest，并拒绝重复逻辑列。projection digest 有意只表达列名与内容，不能代替这些来源关系检查。
3. **独立 expected。** 从实际 cells 算出的 digest 必须等于 Spec.PriorInput 的 expected digest；保留 work.EvaluationKey 与 batch/spec 的一致性检查。不能把 expected 改写为刚计算的值来“通过”校验。
4. **有界输入。** 工厂拒绝 null 列表、超过 128 个 cell、null 成员与重复列；继续依赖不可变 cell 已验证的标识符与 ContentDigest，不能放入未验证的占位值。空列表可以产生空 projection digest。

沿用仍有含义的错误码：`FirstRowPriorInvalid`、`PriorProjectionMissing`、`PriorViewMismatch`、`PriorProjectionMismatch`、`WorkPriorMismatch`、`BatchAuthorityMismatch`。仅因已删除的 batch projection 缺失而触发的状态直接消失，不增加替代 sentinel。

### wrapper 字节上限的处理

当前 Create 还检查完整 projection wrapper 不超过 64 KiB。删除 wrapper 后不为这项检查重新序列化旧对象：保留 128 列、每列标识符最多 128 UTF-8 bytes 和固定 64-hex digest 的输入约束即可。

在当前禁止控制字符及 encoder 规则下，用每个合法标识符 UTF-8 byte 最多膨胀为 3 个 JSON bytes 保守估计，完整旧 wrapper 不超过 `115 + 128 × (41 + 3 × 128 + 64) + 127 = 62,834` bytes，低于 65,536。该界限依赖当前字段形状与编码规则，不变成新的运行常量。

规划时用当时已构建生产类做了独立 .NET 实验：128 个唯一列名，每个恰为 128 UTF-8 bytes；内部大量 U+00A0 的 wrapper 为 **60,786 bytes**，U+2028、反斜杠、双引号三种样本各为 **45,426 bytes**。这些是边界样本证据，不冒充穷举所有字符。实施时将相应输入边界覆盖纳入现有测试。

`MaximumProjectionCanonicalUtf8Bytes` 仍被 `EvaluationKey.DecodeCanonical` 使用，本切片保留该常量及其余调用，不能按名称全局删除。

## 5. 实施包与验证

| 包 | 改动与完成条件 |
|---|---|
| A：计算入口与运行链 | 先固定基线 digest，再贯通 Abstractions → Manager → Runtime → Getter；删除两模型和冗余传递，保持所有剩余校验 |
| B：消费者与测试 | 同步 Runtime/Store/Getter/Hosting/CLI 等测试夹具；移除仅证明已删 wrapper roundtrip 的测试，保留其余持久 golden |
| C：集成审阅与文档 | 搜索残留，验证输出和持久数据不变，更新当前概念/运行说明；不改写历史 API evidence |

API 与调用方一次收口，不提交需要长期保留两种 batch 构造形状的过渡版本。测试迁移可以分工，生产 hash 编码只有一个负责人。

| 场景 | 必须证明 |
|---|---|
| 固定基线真实 cells → digest | 与旧 Create 得到的值逐字相同；空 projection、单列、多列均覆盖。不得用新函数生成 expected |
| 相同列名/内容，不同 cell 来源或 definition | digest 相同；列顺序、列名或内容变化会改变 digest。FirstRow 与空 projection 的 EvaluationKey 不同 |
| 前驱缺失、成员数量/顺序/身份不匹配、重复列 | 既有拒绝语义保留，实际模型调用次数为零 |
| cells 与 expected digest 不同；work 与 Spec 不同 | 保留独立比较与对应错误；不能只验证最终模型调用成功 |
| 128 列与 Unicode/转义字符边界、129 列/null/重复列 | 合法极值接纳，非法输入仍拒绝；不保留完整 wrapper 只为测上限 |
| 既有 EvaluationKey/Cell/Row/Fulfilled golden | canonical bytes、digest 与版本不变；只删除已退休 projection wrapper 自身的 golden 项 |
| 已有缓存 winner，重开后继续构建 | 缓存仍命中，不重新生成已提交 cell；逐行推进与并发结算不变 |
| Runtime rendering / Hosting 证据 | 相同 batch 的模型消息和现有 digest 字段不变 |
| 非空 RecapGrid 的 Prepared 冷恢复 | 原 canonical request、commitment、ExactContextInputs 不变，零 recap 生成调用 |

至少一条 Manager → 实际 Runtime → 假 provider 的多列前驱场景覆盖完整生产链；同时复用现有 Getter provenance、Store first-winner 与 Galatea 非空恢复测试。无需为这个无格式变更切片再建设 ZIP 状态捕获或迁移工具。

直接受影响的测试树已找到：`SessionJournal.RecapGrid.Abstractions/Runtime/Getter/Store/Hosting/WalkingSkeleton.Tests`（斜线表示分别对应各项目）、`SessionJournal.Cli.Tests`、`Galatea.RecapGrid.Tests`、`Galatea.Server.Tests`。Manager 的实际主链另由 `SessionJournal.RecapGrid.Manager.Tests` 回归；按最终符号搜索补齐自然消费者，不预设整个仓库全测。

重 .NET 验证串行使用 `--no-restore -m:1 -nr:false`；Server/CLI 独立 build；Galatea 用 [E2E 指南](e2e-testing.md#离线与非-live-命令)的明确 Live 类过滤。规划阶段仅做源码实验，实施验证单独记录；不部署或操作真实实例。

## 6. 后续路线已修订：丢弃旧 Recap，最后统一重建

用户在本切片完成后明确接受：旧 Recap 内容和缓存命中可以全部放弃，所有重构代码完成后最后统一调用 LLM 重建，不要求正文相同。原先的整图 converter、old projection→正文映射、保全部孤立 winner 与一次旧格式转换要求已撤销。

下一实施入口为[Store 简化计划](recap-store-simplification-plan.md)：普通随机 Cell/Row ID、`CellSlot(recipe, historyRow, column)`、SQL 一份持久数据。它将删除本切片暂留的 `FromCells`、Content/Prior/Evaluation digest 和独立 EvaluationKey，取消跨 recipe 的自动内容等价复用；保留同 Slot 首个结果、行前沿、显式 Overlay Reuse 及必要关系校验。

Timeline、Cadence、Control 格式先保持，后续再单独收口仍保留数据的身份。Journal、Prepared 固定正文、规则和 Control 生效回执不属于可清缓存。最终清库前还须正常收敛依赖旧 Store proof 的 pending promotion，具体条件见[总设计 §7](identity-simplification-design.md#7-保留范围与最终重建边界)。

本轮只更新后续文档，不删除数据或调用模型。下面的旧 digest golden 与 1,454 项结果是已完成切片的历史证据，不是新 Store 的兼容要求或完成证据。

## 7. 当时的辩证裁决

三位 reviewer 分别从需求、最小架构、语义保护出发独立查源码；第二轮比较大迁移与无格式前置切口，第三轮只裁决“删 wire”是否应进一步收口为“删完整重复对象”。主线程核对消费者并补做字节边界实验。

| 争点 | 裁决 | 依据与修订 |
|---|---|---|
| 下一步必须立即联合整图迁移 | 修订 | 联合改格式仍需一起发布；本次零格式删对象不会产生第二次迁移 |
| 只删无消费者 decoder | 扩为完整对象删除 | 单删 decoder 太零碎；删除 batch 重复输入才形成完整可交付链 |
| projection wire 完全无人调用 | 撤回原判断 | Runtime 有一次真实 canonical 比较；审查其前置校验后才决定删除 |
| 两个公开 projection 模型、重复 batch/plan 字段与 wrapper | delete | 没有独立输入权威或正文消费者；已有 cells 与 Spec 足够 |
| 工厂、Manager、Getter 的投影构造 | merge | 在既有 digest 类型提供一个 FromCells 计算入口 |
| 前驱成员关系、expected/work 校验、原 hash body/domain | keep | 删掉会混用前驱或令现有缓存失效、重调已提交结果 |
| 新迁移器/双 reader、旧 wrapper fallback | defer / 不引入 | 本切片没有任何持久格式变化，无实际消费者 |
| 联合迁移默认 GC、所有旧 Control 操作必须先执行完 | simplify | 默认全量保留；仅处理编码确实受影响的 pending，避免无关副作用 |

已删除 **2 个公开瞬态模型、2 个重复传递字段，以及完整 wrapper 的编码缓存/解码/对账路径**；增加 1 个现有 digest 类型上的计算方法。内部 hash body 与持久 digest 暂留，不以代码行数或 hash 清零作为完成指标。没有需要用户额外裁决的本切片产品问题。

## 8. 实施记录（2026-09-14）

代码与测试提交：`258125fd`。

生产改动在 7 个文件收口：删除 `PriorInputProjection`、`PriorProjectedContent`、完整 wrapper DTO、
`FrozenRowBatch.PriorProjection` 与 `DerivedRowPlan.Projection`，新增既有 digest 类型上的 `FromCells`。
Manager、Runtime 和 Getter 共用这个入口。内部 body DTO、domain、canonical encoder、持久 schema、
Runtime rendering 与 Hosting 证据字段保持；没有 adapter、双 reader、迁移工具或模型能力变化。

删除前，用旧已编译生产程序集和真实 cells 捕获了空、单列、双列固定 digest，写入
`PriorInputProjectionDigestTests`（见提交 `258125fd`；Store v3 删除对应 digest 后该测试退役）。
单列正文为 `alpha content`，双列第二列含中文、引号和反斜杠；expected 是旧实现输出的固定值，
不是新方法生成。现有 EvaluationKey/Cell/Row/Fulfilled 的 canonical 与 digest golden 保持原值；
只删除已退休 projection wrapper 自身的 canonical 项。

[RuntimePriorValidationTests](../../tests/SessionJournal.RecapGrid.Runtime.Tests/RuntimePriorValidationTests.cs)
覆盖缺前驱、数量/列名/definition/cell 不匹配、错序、重复列、独立 expected、work 与 FirstRow 校验，
拒绝时 resolver/provider 均为零调用。work 的 prior 不一致会先触发现有 `WorkAuthorityMismatch`；
后面的防御性 `WorkPriorMismatch` 保留，不为强行命中它伪造非法私有对象。

[ManagerRuntimePriorInputTests](../../tests/SessionJournal.RecapGrid.Manager.Tests/ManagerRuntimePriorInputTests.cs)
补齐真实 Manager → Runtime → 假 provider 的多行两列链，逐行检查模型输入中的前驱列顺序和正文；
关闭并重开 Manager/Runtime 后，在零调用预算下读取既有结果。此测试保持 Journal owner 打开，
不把它当作完整进程重启证据；非空 Prepared 冷恢复继续由现有 Galatea 恢复套件验证。

生产与测试经交叉只读审查，无阻断发现。测试迁移没有删减 Store first-winner、Manager missing-only
与 row-frontier 回归。该已完成切片的持久 hash 数量不变；当时的联合迁移后续路线已由 §6 的新需求替代。

最终串行验证结果：

| 测试项目 | 通过 |
|---|---:|
| SessionJournal.RecapGrid.Abstractions.Tests | 28 |
| SessionJournal.RecapGrid.Manager.Tests | 79 |
| SessionJournal.RecapGrid.Runtime.Tests | 66 |
| SessionJournal.RecapGrid.Getter.Tests | 29 |
| SessionJournal.RecapGrid.Store.Tests | 55 |
| SessionJournal.RecapGrid.Hosting.Tests | 29 |
| SessionJournal.RecapGrid.WalkingSkeleton.Tests | 27 |
| SessionJournal.RecapGrid.Manager.PublicSurface.Tests | 3 |
| SessionJournal.RecapGrid.Runtime.PublicSurface.Tests | 4 |
| Galatea.RecapGrid.Tests | 9 |
| SessionJournal.Cli.Tests | 142 |
| Galatea.Server.Tests（明确排除 3 个 Live 类） | 983 |
| **合计** | **1,454** |

以上项目均为 0 失败、0 跳过，使用 `dotnet test tests/<项目>/<项目>.csproj --no-restore -m:1 -nr:false`；
Galatea 附加 [E2E 指南](e2e-testing.md#离线与非-live-命令)中的环境清理、精确类过滤与
`xUnit.MaxParallelThreads=4`。RecapGrid、Server 和 CLI 独立 build 均为 0 warning、0 error。
文档检查 `python3 scripts/check_session_journal_docs.py` 为 36 文件、0 diagnostics，diff 检查通过。

实施中新增 Manager 测试首次把两轮对话误当两行，实际夹具有 5 个 Timeline 行；修正为按实际行数
逐行验证前驱内容代次后，focused 测试及完整 Manager 套件均通过。最终统计仅计算完整套件一次。
本轮是本地、无真实 provider 的验证，没有部署或修改真实会话数据。
