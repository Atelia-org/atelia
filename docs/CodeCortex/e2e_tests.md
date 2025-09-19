一套专为“不可变 SymbolIndex + IndexSynchronizer 增量同步”设计的端到端测试清单，每个用单独命令、全默认参数、可一键/CI运行，覆盖快照一致性、增量正确性与搜索层级行为。

## 测试命令清单（每项一个命令，均零参数）

- e2e-add-method
  - 目标：验证增量“新增 public 成员”被捕获，Outline 更新，基础能力健康检查（已实现）。
  - 断言：Outline 中出现新增方法名；回滚成功。

- e2e-doc-removed
  - 目标：验证 DocumentRemoved 被正确处理（修复点 P1），类型从索引移除，命名空间按保守策略级联移除。
  - 步骤：删除 e2e 中标记文件（包含 E2E_INSERT_HERE 的类型文件）；轮询 Search/Outline。
  - 断言：
    - Search("T:Ns.Type") 总数=0
    - Search("Ns.") 不包含被删类型；若该命名空间成为空，则 "N:Ns" 也应被移除（级联/保守检查）
  - 回滚：还原文件。

- e2e-partial-type-remove
  - 目标：验证 partial 类型跨文档映射（_typeIdToDocs/_docToTypeIds）逻辑：删一份不移除，删完才移除。
  - 步骤：在同一类型上临时创建第二个 partial 文件；删除第一个文件→仍存在；删除第二个→类型被移除；回滚两份。
  - 断言：第一次删除后 Search 命中仍≥1；第二次删除后命中为0；必要时验证命名空间级联。

- e2e-namespace-deep-cascade
  - 目标：验证深层命名空间链的保守级联移除（通过 Current.Search 前缀分支检查）。
  - 步骤：在 Ns1.Ns2.Ns3 下仅有一个类型；删除该类型文件。
  - 断言：依次移除 N:Ns1.Ns2.Ns3，然后在上层若无剩余子项时继续移除，直到遇到仍有子项的层为止。

- e2e-project-removed
  - 目标：验证 ProjectRemoved 影响下，项目内所有文档类型从索引清理。
  - 步骤：备份 .sln；暂时注释/移除一个子项目条目；重载 Workspace；轮询。
  - 断言：该项目内类型/命名空间均不可搜索；回滚 .sln。
  - 说明：命令默认只在本地运行；CI 可选择跳过或放夜间计划，避免因 MSBuild 差异导致不稳定。

- e2e-wildcard-search
  - 目标：验证通配符（ContainsWildcard + Regex 转换）路径。
  - 步骤：执行 Find("*Target*")、Find("E2E.*.Type?") 等。
  - 断言：命中顺序与 MatchKind.Wildcard；确保非空并与预期集合重叠。

- e2e-generic-base-search
  - 目标：验证 GenericBase 索引（_byGenericBase），与 FQN 泛型去噪匹配。
  - 步骤：准备一个泛型类型与多个实例化/嵌套；Find("List") 或目标类型基名。
  - 断言：MatchKind.GenericBase 的结果存在；数量≥1；且同名非泛型不被误杀。

- e2e-case-insensitive-exact
  - 目标：验证 FQN 忽略大小写字典路径（_fqnIgnoreCase），返回 MatchKind.ExactIgnoreCase。
  - 步骤：输入大小写错位的 FQN。
  - 断言：命中单项；MatchKind=ExactIgnoreCase；排序靠前。

- e2e-suffix-ambiguity
  - 目标：验证简单名后缀匹配的歧义标记（IsAmbiguous），仅在无点的查询触发。
  - 步骤：准备两个不同命名空间的同名类型；Find("TypeA")。
  - 断言：返回多项且被标记 IsAmbiguous=true（文本模式可检“!ambiguous”，或 json 模式解析布尔字段）。

- e2e-fuzzy-fallback
  - 目标：验证 Fuzzy 仅在前序策略无命中时生效（ComputeFuzzyThreshold）。
  - 步骤：使用简单名的 1 字符误差；确保不存在其他精确/前缀/包含命中。
  - 断言：MatchKind.Fuzzy；且当添加一个真实命中项后，再次查询不应进入 Fuzzy。

- e2e-with-delta-upsert-order
  - 目标：验证 WithDelta 先移除后添加的“更新/改名/移动”场景会产生正确最终快照。
  - 步骤：将类型重命名（或移动命名空间），这会生成 TypeRemovals + TypeAdds。
  - 断言：旧 DocId 不可搜索，新 DocId 可搜索；FQN 映射更新，GenericBase 桶更新。

- e2e-assembly-null-for-namespace
  - 目标：验证命名空间的 Assembly 在 ToHit 规范化为 null。
  - 步骤：Find("N:Foo.Bar") 或 FQN 搜索命名空间。
  - 断言：命名空间项 Assembly==null；ParentNamespace 有值或为空字符串统一规范（对外 null）。

- e2e-debounce-batch
  - 目标：验证 debounce 合并：在 DebounceMs 内多次更新仅产生一次可观测的快照更替。
  - 步骤：快速连续插入三次无害变更（例如方法体 Console.WriteLine 文本不同）；在每次写后不要立即读，等窗口结束后再读。
  - 断言：最终 Outline 直接从初始跳至最终（中间状态不可见）；可选解析 DebugUtil“IndexSync”日志（通过 ATELIA_DEBUG_CATEGORIES=IndexSync），期望仅 1 次 Batch 应用。

## 实现约定（统一规范）
- 命令位置：CodeCortexV2.DevCli；每项一个命令名，零参数、全默认。
- 目标工程：默认 CodeCortex.E2E.sln，目标类型或命名空间固定；必要时在 e2e 工程中放置明确的 E2E_INSERT_HERE 标记。
- 等待策略：轮询式（每 200–300ms，一共 ≤ 60 次）；必要时重建 WorkspaceTextInterface 确保新加载；最后必回滚。
- 断言输出：失败非零退出码，明确原因；成功打印简要 PASS，并输出少量上下文（如 Outline 片段/命中计数）。
- 文本/JSON：针对需要读取 IsAmbiguous 等字段的用例，Find 使用 `json=true` 并解析布尔字段，避免文本脆弱匹配。

## 推荐落地顺序（易→难）
1) e2e-doc-removed、e2e-partial-type-remove、e2e-namespace-deep-cascade（核心增量与级联）
2) e2e-suffix-ambiguity、e2e-generic-base-search、e2e-case-insensitive-exact、e2e-wildcard-search、e2e-fuzzy-fallback（搜索层级与行为）
3) e2e-with-delta-upsert-order（改名/移动的正确性）
4) e2e-project-removed、e2e-debounce-batch（系统层与性能相关，略复杂）

## 小结
- 每个场景一个命令，默认参数、自包含回滚，适合本地/CI 一键跑。
- 覆盖 SymbolIndex 的不可变快照与预计算索引、IndexSynchronizer 的增量差分与级联移除，以及 Search 的分层匹配与排序规则。
