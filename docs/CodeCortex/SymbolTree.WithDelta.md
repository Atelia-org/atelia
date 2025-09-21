## 目标
一套面向增量、可演进到生产的设计方案，用于实现 `SymbolTreeB.WithDelta`：目标是在不做全量扫描的前提下，对当前不可变快照进行“闭包完备”的 delta 应用，保证幂等与局部性，同时为后续优化留出扩展点。

## 构建操作直接相关源码
  - (src\CodeCortexV2\Abstractions\ISymbolIndex.cs) 关键，目标接口定义
  - (src\CodeCortexV2\Abstractions\SymbolsDelta.cs) 关键，输入数据定义
  - (src\CodeCortexV2\Abstractions\SymbolEntry.cs)
  - (src/CodeCortexV2/Index/SymbolTree/SymbolTree.Core.cs) 关键，要构建出的不可变类型的数据结构定义
  - (src/CodeCortexV2/Index/SymbolTree/SymbolTree.Build.cs) 关键，要实现`SymbolTreeB.WithDelta`的位置，现有的`SymbolTreeBFromEntries`函数的实现
  - (src/CodeCortexV2/Index/SymbolTree/Node.B.cs)
  - (src/CodeCortexV2/Abstractions/MatchFlags.cs)
  - (src/CodeCortexV2/Index/Synchronizer/IndexSynchronizer.cs) 构建SymbolsDelta实例的具体实现

## 查询操作相关源码-次要
  - (src/CodeCortexV2/Index/SymbolTree/SymbolTree.Query.cs) 查询操作实现位置
  - (src/CodeCortexV2/Index/SymbolTree/QueryPreprocessor.cs) 查询预处理器实现

## 现状回顾与约束

- 树结构
  - `NodeB` 为不可变节点，字段：Name、Parent、FirstChild、NextSibling、Kind、Entry（每节点最多 1 个 Entry）。
  - 同名类型在不同程序集可出现为多个兄弟节点（以 Entry.Assembly 区分）。
  - 根节点是一个 `Namespace`，Name == ""，Parent == -1（FromEntries 中约定 index 0 即 root）。

- 别名层（Alias layer）
  - 两层字典：`_exactAliasToNodes`、`_nonExactAliasToNodes`，值为 `ImmutableArray<AliasRelation>`。
  - 生成规则（FromEntries 已明确）：
    - Namespace 段：Exact 使用段名本身；NonExact 使用小写段名 + IgnoreCase。
    - Type 段：
      - 非泛型：Exact 用 baseName；NonExact 用 lower(baseName) + IgnoreCase。
      - 泛型（bn`n）：Exact 用 "bn`n"；NonExact 三类：
        1) baseName + IgnoreGenericArity；
        2) lower(baseName) + IgnoreGenericArity|IgnoreCase；
        3) lower("bn`n") + IgnoreCase。
  - 别名仅定位到“节点”，不是“Entry”；Query 层按右至左约束父子关系。

- Delta 合同（来自 ISymbolIndex 与 SymbolsDelta）
  - Producer（同步器）已做闭包与一致性处理：
    - Adds 确保祖先命名空间链存在；Removals 会包含批次导致为空的命名空间（含保守级联）。
    - Rename 表示为 remove(oldId)+add(newEntry)。
  - Consumer（WithDelta）只需“局部应用”，不做全局推断或扫描。可做轻量防御校验并打印诊断（DebugUtil），但不能全树遍历。

- 搜索逻辑
  - Query 层用别名桶获得候选节点，再做父链约束修剪；不做“子树展开”来解释 GenericBase 锚点。

## WithDelta 的总体策略

- 顺序：先删后加。
  - Removals 保证不留悬挂别名/指针；Adds 依赖 Removals 后的结构。
- 不全量复制：
  - 节点数组采用“按需拷贝”：只对受影响的节点（父、被删/新建节点、以及其前驱兄弟）重建 `NodeB` 实例；绝大多数节点原位复用索引与对象实例，保持索引稳定。
  - 别名字典只对受影响的 key 做不可变数组的替换；其他 key 原样复用。
- 不“重新推断”闭包：
  - 不会逆向扫描来发现新的空命名空间；仅按 delta 描述操作。
  - 可选 Debug 校验：当删除 N: 命名空间时，若仍有子节点则打印诊断。

## 新增共识要点（2025-09-20）

- 防“幽灵命中”的别名同步删除：类型/命名空间删除时，必须同步移除该节点生成的所有别名（类型的 bn、bn`n、lower 组合；命名空间的原名与 lower）。文档中的 RemoveTypeAliases/RemoveNamespaceAliases 为必做步骤。
- Upsert 原则与确定性：当 Add 命中同父同名且 Entry.SymbolId 相同的节点时，应原位 UpdateEntry（幂等不重复建节点）；别名列表需去重，且建议按 NodeId 升序排序以提升快照确定性（非功能性，但有助调试和测试稳定）。
- 删除查找的按需小索引（P1）：首版可用兄弟链扫描保证正确性；优化时仅为本次 delta 涉及的父节点/名称构建“父级子表索引”，或为涉及的 docId 构建临时映射，避免全量或全局索引。
- NodeBuilder 的 COW 形态（P1）：除当前 List<NodeB> 拷贝外，可引入“ImmutableArray 原始 + Dictionary<int, NodeB> modified”的惰性覆写以减少大快照的拷贝成本，小 delta 场景下更省内存。
- 别名层原子性：无需复杂事务接口；采用“首次写入时浅拷贝顶层字典 + 针对变更 key 生成新的 ImmutableArray 值”的策略即可，严禁在旧快照字典或其值上就地修改。
- 墓碑压缩触发策略（P2）：采用“比例阈值 + 绝对数阈值”双条件，例如当墓碑占比与数量同时超标时触发压缩/重建；压缩将批量重写别名桶，建议作为后台维护或上层触发的全量重建路径。

## 需要的内部增量 Builder（仅 WithDelta 内部使用）

为实现“局部拷贝 + 指针修补”，在 `WithDelta` 中引入轻量 builder（不暴露为公共 API）：

- NodeBuilder（快照内可变视图，索引保持不变）
  - 持有一个 `List<NodeB>`，初始为 `_nodes` 拷贝（浅拷贝引用即可，因为 NodeB 是 struct，复制不可避免；但我们只会改动少数索引，内存仍是局部的）。
  - GET：读取父、子、兄弟等字段无需额外结构。
  - SET：
  ## 目标
  `SymbolTreeB.WithDelta` 的增量应用策略（生产可用）：仅依赖叶子级变更（TypeAdds/TypeRemovals），在索引内部完成命名空间的按需创建与级联删除；避免全量扫描，保证幂等与局部性，并与 `FromEntries` 的别名与节点命名规则保持一致。
    - UpdateEntry(nodeIdx, SymbolEntry? newEntry)
  ## 现状回顾与约束
  -- Delta 合同（来自 ISymbolIndex 与 SymbolsDelta）
    - 仅叶子级 delta：Producer（例如 IndexSynchronizer）优先仅产生 `TypeAdds` 与 `TypeRemovals`；`NamespaceAdds`/`NamespaceRemovals` 已弃用，可能为空或被忽略。
    - Rename 表示为 remove(oldId)+add(newEntry)。
    - Consumer（WithDelta）需要在本地内部完成：
      - 对新增类型的“命名空间链按需创建”；
      - 对删除导致的“空命名空间级联删除”（放在流程末尾统一执行）。
    - 不做全量扫描，保持局部性与幂等性。
      - FindChildrenByName(parent, kind, name): 返回同名列表（类型场景下可能多实例：跨程序集）。
  ## WithDelta 的总体策略
  顺序：先删后加，最后统一做“命名空间级联删除”。
  不全量复制：
    - 节点数组按需复制；别名字典 shallow copy 顶层字典，按 key 替换桶（ImmutableArray）。
  不“重新推断”全局闭包：
    - 空命名空间的判断仅在受影响祖先集合内进行；不做全树扫描。
      - 取出现存 `ImmutableArray<AliasRelation>`，转临时 `List<AliasRelation>`；
  ## 新增共识要点（2025-09-22）
  - Upsert 原则与确定性：当 Add 命中同父同名且 Entry.DocCommentId 与 Assembly 相同的节点时，原位更新 Entry（幂等）；否则允许并存同名兄弟类型节点（跨程序集）。
      - 回写新的 `ImmutableArray<AliasRelation>` 给对应 key；空集合时可直接移除此 key（节省空间）。
  ## 具体流程（三阶段）
  - 删除阶段
    - 仅处理 `TypeRemovals`：
      - 通过最后一段 exact 别名桶定位候选节点（bn 或 bn`n），以 `Entry.DocCommentId` 精确比对删除，避免误伤。
      - 对每个被删除的类型节点，先移除类型别名，再从父链表摘除该节点（修补父/兄指针）。
      - 收集“级联删除检查点”：记录最近的命名空间祖先，用于末尾统一判断是否需要删除空命名空间。
  - 添加阶段
  - 若当前 `_nodes` 为空（初次构建）：先创建根节点 idx=0（Name=""，Parent=-1，Kind=Namespace，Entry=null）。
      - 不再处理 NamespaceAdds：命名空间链由消费者内部按需创建。
    - Types（for each entry in delta.TypeAdds）：
      - 先确保命名空间链存在：逐段 GetOrAdd Namespace，缺段时创建并添加命名空间别名。
      - 再类型链：
        - 非叶类型节点仅创建结构节点（Entry=null，不添加别名）。
        - 叶类型节点写入 Entry，并依照 `FromEntries` 规则添加类型别名。
        - 同父同名时：若已有同 docId+Assembly 的节点则原位更新 Entry；否则创建并存兄弟节点（跨程序集场景）。
  - 末尾级联删除阶段
    - 对收集到的命名空间检查点去重，逐个自下而上判断空命名空间：
      - 若整棵子树不含任何带 Entry 的类型节点，则先 DFS 移除该子树全部别名，再将该命名空间节点从父链表中摘除。
      - 碰到非空或根节点即停止。

  -- 收尾与封装
    - 修补父链，如上。
    - 别名：AliasBuilder.RemoveNamespaceAliases(node.Name, nodeId)。
    - 置空 Entry，并断开 NextSibling；Kind/Name 保留（墓碑）。

  ## 测试清单（单测优先）
  - 级联删除命名空间（由 consumer 内部实现）：删除后父层仍正确联通（firstChild/nextSibling 指针无误）。
  ## 与 FromEntries 的衔接
  - 空快照优化：当当前快照为空且只有 TypeAdds 时，WithDelta 会从 TypeAdds 合成命名空间条目（N:），再统一调用 `FromEntries` 构建，以避免依赖已弃用的 NamespaceAdds/Removals 字段。
    - 在叶节点：
      - 若 Entry 为空：UpdateEntry(nodeId, entry)。
      - 若 Entry 非空但非同一 docId（理论不应发生，因命名空间 docId 唯一），则覆盖为最新值（或 Debug 日志）。
  - Types（for each entry in delta.TypeAdds）：
    - 先命名空间链：`entry.ParentNamespace` -> 逐段 GetOrAdd Namespace。
    - 再类型链：
      - 用 `entry.SymbolId` 拆得 `typeSegments`（DocId 严格形式，最后段名可能为 bn`n）。
      - 对除最后段外的每段：GetOrAdd(Type, seg)（Entry=null）。
      - 最后一段：
        - 获取 `sameName = FindChildrenByName(parent, Type, lastName)`。
        - 在 sameName 内查找是否已有 Entry?.SymbolId == entry.SymbolId（必要时连 Assembly 比较，以防 workspace 极端情况）；若找到：
          - 幂等：若 Entry 完全一致 -> no-op；否则 UpdateEntry(nodeId, entry)（认为“upsert”）。
        - 否则新建：NodeBuilder.NewNode(lastName, parent, Type, entry)；AliasBuilder.AddTypeAliases(lastName, newId)。

- 收尾与封装
  - 将 NodeBuilder 的 List<NodeB> 封装为 `ImmutableArray<NodeB>`。
  - AliasBuilder 提供最终 `Dictionary<string, ImmutableArray<AliasRelation>>`（exact / non-exact），仅替换发生过变更的 key；其他 key 仍共享旧实例，达到局部拷贝的效果。
  - 返回新的 `SymbolTreeB` 实例。

## 幂等性与局部性说明

- 幂等性：
  - 删除：按 docId 严格匹配，重复删除找不到节点即 no-op。
  - 添加：末段按 docId 精确去重（必要时连 Assembly 判等），避免同一增量重复导致的重复节点。
- 局部性：
  - 路径查找仅访问“路径上的祖先 + 目标父的子链表”；不做全量扫描。
  - 别名字典仅对受影响 key 做不可变数组的替换，未触及的 key 保持引用共享。

## 线程安全与并发保证

- 快照不可变：`SymbolTreeB` 的节点数组与别名字典均为不可变表示（或在语义上只读），旧快照在 `WithDelta` 执行期间不会被修改，支持并发读。
- 增量构建隔离：`WithDelta` 在局部复制/新建受影响的数据结构后，封装为新的快照对象返回；确保顶层字典实例替换且 value 为新 `ImmutableArray`，避免写入旧实例。
- 并发 `WithDelta`：允许对同一旧快照并发执行多个 `WithDelta` 调用（它们彼此独立）；但实现中不得使用跨实例的可变全局缓存（例如静态临时索引）。

## 复杂度粗略估计

- 设 Delta 中总段数为 S（命名空间 + 类型链），兄弟数平均为 B：
  - 添加/删除一个符号：O(S + B)（B 项来自父子链表的扫描与指针修补；可用父级子表索引将其摊销到接近 O(S)）。
  - 别名更新：对每个新/删节点，常数个 alias key 更新（Namespace：≤2；Type：≤4），每个 key 的列表操作为 O(候选节点数量)；一般很小。
- 内存：
  - NodeB 局部拷贝（只改动的节点 + 少量新节点），别名字典仅替换少数 value（ImmutableArray）。

## 边界与防御性校验

- 初次构建：_nodes 为空时，创建 root。
- 非法 docId 或路径缺失：按 delta 的“权威性”处理——添加路径缺段会被创建；删除路径不存在则 no-op，并 DebugUtil 打印（类别：SymbolTreeB.Delta）。
- 命名空间删除时仍有子节点（未被本批次删除）：不做全树扫描；在 Debug 模式下探测 node.FirstChild != -1 则打印（SymbolTreeB.Delta），说明 producer 闭包不完整。
- 名称大小写与泛型：
  - 链式比对使用 Name 精确匹配（bn 或 bn`n），区分大小写（与构建时一致）；别名层负责查询时的宽松匹配。
- 多程序集同名类型：
  - 允许同父、同名出现多个 Type 节点；删除以 docId 为准，会一次性移除该 docId 的所有实例（符合 IndexSynchronizer 的“last declaration removed 才会进入 TypeRemovals”的模型）。

## 可选演进点

- 快路径索引（快照内）
  - docId -> List<nodeId>（Type/Namespace 各一）：WithDelta 中 O(1) 找到末段候选；delta 更新仅改动少量条目。
  - (parentId, kind, name) -> List<childId>：父级子表索引直接常数时间查找；WithDelta 在发生该父下的改动时局部更新。
- Tombstone 压缩
  - 当“墓碑节点”累计超阈值时（或按代数 GC 策略），做一次“子树重建与编号压缩”，并批量重写 alias 数组；作为后台维护任务，不阻塞前台 ApplyDelta。该路径可以重用 `FromEntries` 作为安全退路。
- 大 Delta 退化策略
  - 当 delta 规模超过一定比例（如 > 5% 节点数或 alias 变更 key > 1k），可触发“重建”模式：
    - 方案 A：由上层（IndexSynchronizer）发起 full rebuild（已存在计算分支）。
    - 方案 B：在树内进行“受影响子树重建”，但实现复杂度更高，建议后置。

## 落地优先级（实施顺序建议）

- P0（必须）：核心删/加流程；别名同步维护（避免“幽灵命中”）；基础诊断与日志；先删后加的顺序保证。
- P1（早期优化）：父级子表索引缓存；NodeBuilder 的惰性覆写（COW）形态；按需的 docId/父节点小索引；别名列表的确定性排序。
- P2（后续）：墓碑压缩/重建策略；大 delta 的退化路径与阈值调优；更丰富的一致性检查（限制在受影响子树）。

## 实现小贴士（伪码级）

- 删除类型末段
  - prev = FindPrevSibling(parent, target)
  - if prev < 0: parent.FirstChild = target.NextSibling
    else: prev.NextSibling = target.NextSibling
  - RemoveTypeAliases(target.Name, target.Id)
  - UpdateEntry(target.Id, null); UpdateNextSibling(target.Id, -1)
- 添加新节点
  - newId = AppendNode(name, parent, kind, entry)
  - oldFirst = parent.FirstChild; parent.FirstChild = newId; new.NextSibling = oldFirst
  - 根据 kind 添加相应 alias

## 日志与诊断

- 类别建议：
  - "SymbolTreeB.Delta"：每批次统计与关键操作（+T/-T/+N/-N 数量、节点新增/删除 id 等）。
  - "SymbolTreeB.Alias"：别名桶 key 更新统计（变更 key 数、每 key 新旧元素计数）。
  - "SymbolTreeB.Debug"：可选一致性检查（如 Namespace 删除时仍有子节点）。
- 通过环境变量 ATELIA_DEBUG_CATEGORIES 控制打印级别（参见 AGENTS.md）。

## 测试清单（单测优先）

- 基础构建（初次 full delta）后能搜索到所有 Namespace/Type。
- 增加类型：新增命名空间链 + 新类型；验证 alias 命中（bn、bn`n、lower）。
- 删除类型：删除后搜索不到；命名空间仍存在（若还有其他子项）。
- 级联删除命名空间（由 producer 提供）：删除后父层仍正确联通（firstChild/nextSibling 指针无误）。
- 重命名（remove+add）：删除旧 docId 后添加新 docId，验证路径更新。
- 大小写与泛型：List 与 List`1；ignoreCase 命中与非命中路径。
- 幂等性：对同一 delta 重复调用 WithDelta，结构不再变化。

附加建议：
- 压力测试：连续应用 1000+ 个小 delta，观察时间/内存是否退化，验证别名层与节点层未出现热点退化。
- 一致性测试：随机 delta 序列后，与 `FromEntries` 的全量重建结果对比（节点结构与别名桶集合应一致）。
- 并发安全测试：在高并发读（查询）与并发 `WithDelta` 构建下，验证旧快照可读性与新快照正确性。
- 长时运行与墓碑累积：模拟长时间增量应用，观测墓碑比例与绝对数量，并验证阈值触发后的重建/压缩路径正确性。

## 风险与注意事项（务必落实）

- 别名字典的不变性：返回的新快照必须持有“新”的顶层字典实例，严禁在旧实例上写入；采用“首次写入时浅拷贝”的策略可以同时满足性能与不变性。
- NodeB 更新语义：`NodeB` 为 readonly struct，修改任何字段均需“重建该索引的节点”并覆盖写入；不要尝试原地改字段。
- 删除为墓碑且修补指针：删除节点务必先修补父链/前驱兄弟，再将目标节点置为墓碑（Entry=null, NextSibling=-1）；Parent/FirstChild 可保留，节点将不可达。
- 命名空间删除的一致性：若删除 N: 节点时仍有子节点，仅打印 Debug 诊断，不进行全树扫描或强制清理（闭包由 Producer 负责）。
- DocId 解析准确性：类型嵌套用 '+' 分割，末段可能带反引号 arity；非泛型与泛型段需生成不同 alias 组合（与 FromEntries 保持一致）。
- 多程序集同名类型：删除以 docId 为单位，可能命中多个兄弟节点，需全部删除；添加/upsert 时以 `Entry.SymbolId` 精确判重。
- 别名列表的确定性：可选按 NodeId 升序排序，避免快照之间的非确定性差异（提升调试与测试稳定度）。
- 大 Delta 与墓碑累积：在阈值触发时由上层走 full rebuild；墓碑压缩作为后台维护，避免前台增量路径复杂化。

## 与 FromEntries 的衔接

- FromEntries = 全量构建器；WithDelta 的“新增节点 + 别名规则”与之保持一致。
- 短期可在 WithDelta 内设阈值：当 delta 很大时调用一个“受限 FromEntries”作为临时降级（需要一组 entries 源）；目前 entries 由 IndexSynchronizer 维护的 `_entriesMap` 持有，上层已经存在“失败时 full rebuild”的路径，建议先复用上层。

## 进度（粗粒度）

- 2025-09-21
  - 实施 P0 版本 `SymbolTreeB.WithDelta`：
    - 基于局部路径遍历 + 兄弟链修补，实现删除优先、随后添加/Upsert 的流程；
    - 别名层采用写时复制（per-key 不可变数组替换），同步维护 Namespace 与 Type 的别名项；
    - 节点采用头插法新增子节点，删除时将目标节点“脱链”为墓碑（Entry=null, Parent=-1, NextSibling=-1）；
    - 保持幂等：重复应用同一 delta 结构不变；
    - 初次空快照且仅有 Add 的情况下，P0 退化调用 FromEntries 构建。
  - 后续 P1/P2：
    - P1：父级子表索引、惰性覆写（ImmutableArray + 覆写字典）、按 docId 的临时小索引；
    - P2：墓碑压缩/重建阈值与后台任务、超大 delta 的降级策略。
