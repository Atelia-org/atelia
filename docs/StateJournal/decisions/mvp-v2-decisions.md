# StateJournal MVP v2 设计决策记录

> 本文档记录 StateJournal MVP v2 设计过程中的决策选项和最终选择，从主规范文档分离以保持规范简洁。
>
> 原始文档：`docs/mvp-design-v2.md`
> 分离日期：2025-12-20
> 依据：[秘密基地畅谈会共识](../../agent-team/meeting/2025-12-20-secret-base-mvp-v2-compression.md)

---

## 2. 单选题（决策输入）

填答格式：按题号回复选项，例如：`Q1=B, Q2=A, Q3=C`。

> 说明：这些题按树状分支组织；你本轮只需回答本批题目。

### 2.1 标识与引用

**Q1. ObjectId 的类型与大小？**
- A. `uint64`（推荐：简单、碰撞风险低）
- B. `uint32`（省空间，但上限与碰撞/分配策略更敏感）
- C. 其他：________

**Q2. 对象引用在序列化中存什么？**
- A. 只存 `ObjectId`（读取时通过 VersionIndex 解析到最新 version）
- B. 同时存 `ObjectId + VersionPtr`（读可快一点，但语义更复杂）

**Q3. Resolve 语义（Git 类比：`workspace/working tree` + `HEAD`）？**
- A. "固定快照"：一次打开/一次读取操作绑定一个 epoch 快照（类似 `git checkout <commit>` 后只读视图）（推荐）
- B. "workspace + HEAD"：对未 materialize 的对象，Resolve 按 `HEAD` 解析；对已 materialize 的对象，Resolve 固定返回同一份 workspace 实例（类似 working tree；不会自动 refresh/rollback）

### 2.2 Epoch 与提交协议

**Q4. Commit 的原子提交点（commit point）选什么？**
- A. Superblock 指向 `CommitRecordPtr`（Ptr64：`u64 LE` byte offset，`0=null`，且 4B 对齐；指向一条 Commit Record。在 meta file 默认方案下其物理形态等价于 `MetaCommitRecord`）
- B. Superblock 指向 `RootObjectId` + `EpochSeq`（Commit Record 可选）

**Q5. Commit Record 最少包含哪些信息？**
- A. `EpochSeq + RootObjectId + VersionIndexPtr(Ptr64) + DataTail(Ptr64) + CRC32C`（Ptr64：`u64 LE` byte offset，`0=null`，且 4B 对齐）
- B. `EpochSeq + RootVersionPtr(Ptr64; 直接指向 root 的 version record) + DataTail(Ptr64) + CRC32C`（Ptr64：`u64 LE` byte offset，`0=null`，且 4B 对齐；非 MVP 选择）

**Q6. 是否支持多 root（类似多分支/refs）？**
- A. MVP 仅支持单 root
- B. MVP 支持多个命名 root（需要 refs 表）

### 2.3 VersionIndex（ObjectId -> VersionPtr）

**Q7. VersionIndex 的存储结构选哪种？**
- A. 作为一个 durable object（例如 DurableDict/ObjectMap），本身也版本化
- B. 专用结构（固定布局的 map record），不复用普通 dict

**Q8. VersionIndex 的更新方式？**
- A. 每个 epoch 写一个"覆盖表"（只记录本次变更 ObjectId->NewVersionPtr），查找时链式回溯；周期性 Checkpoint Base
- B. 每个 epoch 写一个"完整表"（写入放大大，但读快；MVP 可能更简单）

**Q9. VersionIndex 查找是否允许链式回溯（最坏 O(chain)）？**
- A. 允许；MVP 通过"周期性 Checkpoint Base / materialize 缓存"控制
- B. 不允许；MVP 强制每次 commit 生成可 O(log n) 查找的结构（更复杂）

### 2.4 对象版本与增量（DiffPayload）

**Q10. ObjectVersionRecord 的组织方式？**
- A. `PrevVersionPtr + DiffPayload`（链式版本）
- B. `CheckpointBasePtr + DiffPayload（显式指向最近 Checkpoint Base）

**Q11. Dict 的 DiffPayload 具体包含什么？**
- A. `Upserts(key->value) + Deletes(keys)`
- B. 仅 `Upserts`，删除通过写入 `null`/tombstone value 表达

**Q12. 读取时 materialize 的策略？**
- A. 加载时把版本链 replay 到内存 dict，之后读完全走内存
- B. 加载时只建索引，读时再按需 replay（更像 lazy，不符合"全量 materialize"主线）

### 2.5 ChangeSet（写入跟踪）

**Q13. ChangeSet 的生命周期与清理点？**
- A. Commit 成功后清空；失败则保留
- B. 每次写入版本 record 后清空（需要更强的失败处理）

**Q14. ChangeSet 是否需要记录"旧值"（用于冲突检测/乐观并发）？**
- A. MVP 不记录旧值（单 writer，简单）
- B. 记录旧值/旧版本（为未来并发/冲突检测铺路）

### 2.6 会卡住编码的缺口：单选题（第二批）

> 目的：把实现会被迫自作主张的部分，提前固化为唯一规格。

**Q19. VersionIndex（`ObjectId -> ObjectVersionPtr`）落地为哪种 durable object？**

- A. 新增专用对象 `DurableObjectMap`：key 为 `ObjectId(varuint)`、value 为 `Ptr64(u64 LE; byte offset)`；overlay 用"有序 pairs + delta key"编码（类似 4.4.2 的 key 压缩，但 key 为无符号）。
- B. 扩展通用 Dict：支持 `ulong` key 与 `Ptr64` value，VersionIndex 复用同一套 Dict diff/apply 代码路径。
- C. 保持 Dict key 仍为 `int32`，VersionIndex 单独做"专用结构（固定布局的 map record）"，不走 Dict 引擎（更偏 Q7B 的路线，但仍可声明为 durable object）。

**Q20. data file 的 record framing 固化为哪一种？**

- A. 与 meta 一致，统一采用 ELOG framing：`[Magic(4)][Len(u32)][Payload][Pad][Len(u32)][CRC32C(u32)]`；并额外要求 data/meta 文件以 `Magic` 作为 frame separator（空文件先写 `Magic`；每次追加 record 后写入尾部 `Magic` 哨兵），以加速双向边界识别。record 类型信息放进 payload（例如第一个字节为 `RecordKind`）。
- B. 采用"Tag + TotalLen + CRC32C"的简化 framing：`[RecordKind:1B][TotalLen:u32 LE][Payload][Pad][TotalLen:u32 LE][CRC32C:u32 LE]`（Pad 使 `TotalLen%4==0`）。
- C. data 保持现状只约束 `TotalLen/CRC32C/4B 对齐`，具体 header/footer 布局由实现决定（不推荐：会导致格式不稳定且难写测试）。

**Q21. `ObjectVersionRecord` 如何携带对象类型/解码规则（避免 reader 无法解析 DiffPayload）？**

- A. `ObjectVersionRecord` payload 头部增加 `ObjectKind:byte`（MVP 仅用 `Dict=1`），reader 以此选择对应的 diff 解码器。
- B. payload 头部增加 `SchemaId:varuint`（可扩展），并约定一张 SchemaId->Decoder 的注册表。
- C. 将对象类型提升到 record kind（即 framing 层的 `RecordKind` 区分 `ObjectVersion_Dict`/`ObjectVersion_VersionIndex`），payload 内只放纯 diff。

**Q22. varint 采用哪一种精确定义与容错策略？**

- A. 采用 protobuf 风格 base-128 varint（ULEB128 等价），要求 **canonical 最短编码**；解码遇到非最短编码/溢出/EOF 直接报格式错误。
- B. 同 A 的编码格式，但解码允许非最短编码（读入后规范化），writer 始终输出 canonical。
- C. 采用自定义 varint（需补充完整规范与测试向量；MVP 不推荐）。

### 2.7 MVP 极简化：Dict Key 与 ObjectId（第三批）

**Q23. DurableDict 的 key 类型（MVP）？**
- A. 仅支持 `ulong` key（推荐：与 `ObjectId`/VersionIndex 完全一致；编码只用 `varuint`；无 ZigZag）
- B. 仅支持 `long` key（允许负数；需要 ZigZag64）
- C. 同时支持 `int32` 与 `uint64`（需要 KeyType 分支；非最简）

**Q24. ObjectId 类型是否维持 `uint64`？**
- A. 维持 `uint64`（推荐：长期无后患）
- B. 改为 `uint32`
- C. 改为 `int32`

---

## 3. 决策表（由问答回填）

| ID | 主题 | 选择 | 备注 |
|---:|------|------|------|
| Q1 | ObjectId 类型 | A. `uint64` |  |
| Q2 | 序列化引用内容 | A. 只存 `ObjectId` |  |
| Q3 | LoadObject 语义 | B. "workspace + HEAD" |  Lazy 创建内存对象；同 ObjectId 同内存对象（存活期内）。语义类比：内存对象=Git working tree（workspace），序列化文件=Git repository；MVP 不设暂存区与远程。 |
| Q4 | Commit point | B. Superblock 指向 `RootObjectId` + `EpochSeq` | 已被 Q16（meta file）覆盖；superblock 属于备选方案 |
| Q5 | Commit Record 内容 | A. `EpochSeq + RootObjectId + VersionIndexPtr + DataTail + CRC32C` |  |
| Q6 | 多 root | A. MVP 仅支持单 root |  |
| Q7 | VersionIndex 结构 | A. 作为一个 durable object（例如 DurableDict/ObjectMap），本身也版本化 |  |
| Q8 | VersionIndex 更新方式 | A. 每个 epoch 写一个"覆盖表"（只记录本次变更 ObjectId->NewVersionPtr），查找时链式回溯；周期性 Checkpoint Base |  |
| Q9 | 回溯链允许性 | A. 允许；MVP 通过"周期性 Checkpoint Base / materialize 缓存"控制 |  |
| Q10 | VersionRecord 组织 | A. `PrevVersionPtr + DiffPayload`（链式版本） |  |
| Q11 | Dict DiffPayload 形态 | B. 仅 `Upserts`，删除通过写入 tombstone value 表达 | 明显这样可以省掉一个集合和一次查找，实现起来更简单，为何会推荐专用Deletes集合方案呢？ |
| Q12 | 读取 materialize 策略 | A. 加载时把版本链 replay 到内存 dict，之后读完全走内存 | 但是对于引用的其他对象只反序列化ObjectId，Lazy加载明确到"不级联反序列化对象引用"语义 |
| Q13 | ChangeSet 清理点 | A. Commit 成功后清空；失败则保留 |  |
| Q14 | 是否记录旧值 | A. MVP 不记录旧值（单 writer，简单） | 后续考虑显式提供某种层级的fork功能 |
| Q15 | varint 适用范围 | B. 除 `Ptr64/Len/CRC32C` 等硬定长外，`ObjectId`/计数/DictKey(`ulong`) 全部用 varint |  |
| Q16 | 元数据布局 | A. 采用 `data` + `meta` 两文件；commit point 为 `meta` commit record 持久化完成 |  |
| Q17 | meta HEAD 定位 | A. 扫尾：打开时从 meta 文件尾部回扫，找到最后一个 CRC32C 有效的 commit record |  |
| Q18 | `NextObjectId` 持久化 | A. 只写在 meta 的 commit record 中 |  |
| Q19 | VersionIndex 落地 | B. 扩展通用 Dict（`ulong` key + `Ptr64` value），VersionIndex 复用 Dict diff/apply |  |
| Q20 | data framing | A. 与 meta 一致，统一采用 ELOG framing（record kind 在 payload）+ `Magic` separator/尾部哨兵 |  |
| Q21 | 版本 record 类型信息 | A. `ObjectVersionRecord` payload 含 `ObjectKind:byte`（MVP 仅 `Dict`） |  |
| Q22 | varint 规范 | A. protobuf 风格 base-128，要求 canonical 最短编码 |  |
| Q23 | Dict key 类型（MVP） | A. 仅 `ulong` key |  |
| Q24 | ObjectId 类型（确认） | A. `uint64` |  |

---

## 4. 设计理由归档（Rationale Archive）

> 本节记录从主规范文档剥离的设计理由，供后续参考。
> 这些内容不影响实现的正确性，但有助于理解设计背景。

### 4.1 varint 与 mmap 的取舍

**背景**：v2 不再以 mmap/zero-copy 为目标。

**理由**：
- v2 选择 varint + 全量 materialize，因此 mmap 对"对象字段级别随机读取"的收益已显著下降
- 引入 varint 并不影响"从任意 record ptr O(1) 跳过整个 record"，因为 record 的 `Len` 仍是固定宽度并位于 framing header
- varint 的代价是：record 内部字段无法靠定长 offset 做 mmap 随机定位；但本 MVP 不追求"对象字段的 mmap 随机读取"

### 4.2 Magic 作为 Record Separator 的设计收益

**设计收益**：
- **概念简洁**：所有 Magic 的语义相同（分隔符），无需区分"Record 内的 Magic"和"尾部哨兵 Magic"
- **fast-path**：reverse scan 初始 `MagicPos = FileLength-4` 可 O(1) 命中
- **resync 统一**：尾部损坏时的 resync 与 fast-path 复用同一套"命中 Magic → 校验 Len/CRC32C"流程
- **代价**：每条 Record 前后各有一个 Magic（首 Record 前的 Magic 是文件头，后续 Record 共享前一条 Record 后的 Magic）

### 4.3 tombstone vs Upserts + Deletes(keys) 的权衡

**Q11 选择 B（tombstone）的理由**：
- apply diff 时只需一次查找/一次分支：tombstone => remove，否则 set
- 序列化格式也更简单：每条 entry 自带一个 value tag（Set/Null/Tombstone/Ref/Int/...）

**为什么有时更推荐 Upserts + Deletes(keys)**：
- 它避免引入一个"保留值编码"（tombstone），特别当 value 编码空间很紧、或希望 `null` 作为唯一特殊值时
- 在一些对象类型中，"删除"和"设置为空"容易混淆；显式 deletes 集合把语义分支变成数据结构层面的分离

### 4.4 LoadObject 返回 null 的设计理由

返回 `null` 而非抛异常的理由：
- "对象不存在"是合法的查询结果，不是错误状态
- 允许调用方用 `LoadObject(id) ?? CreateNew()` 模式处理新建/加载的分支
- 与 `Dictionary.TryGetValue` 风格一致

### 4.5 Dirty Set 存在的必要性

**为什么仍需要 Dirty Set（强引用集合）**：
- 本 MVP 规定"引用在内存与磁盘都用 `ObjectId` 表示"
- 因此，root/容器中保存的只是 `ObjectId`，它不会形成对被引用对象实例的强引用
- 若某对象已经 materialize 且被修改为 dirty，但调用方暂时不再强引用它（例如只把它的 `ObjectId` 放进某个 dict/字段），则该对象实例可能被 GC 回收
- 如果没有 Dirty Set，commit 时就无法再拿到它的 ChangeSet，导致"逻辑上可达（通过 ObjectId）但修改丢失"

### 4.6 CommitAll 提交全部 Dirty 对象的理由

**目的**：避免出现"root 语义可达但中间节点未 materialize，导致可达 dirty 被漏提交"的情况。

**代价与接受理由（MVP）**：这可能将"临时对象/最终不可达对象"的修改也持久化到 data 文件中；但 MVP 不做 GC/回收，因此这是可接受的空间换正确性取舍。

### 4.7 刷盘顺序的崩溃安全性

刷盘顺序为"先 data 后 meta"的理由：这样即使崩溃，恢复时最多丢失最后一次 commit（meta 没落盘），不会出现"meta 指向不存在的数据"。
