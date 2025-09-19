# CodeCortex Phase 1 实施指南 (L0+L1+L2+L3+L5 Outline-Only)

> 目标：在不引入语义生成 (L4)、AST 编辑 (L6)、调查任务 (L7) 的前提下，交付一个稳定的 **本地 .NET 代码 Outline 增量服务层**，支持 CLI 与 JSON-RPC 查询、增量缓存与 Prompt 窗口（仅 Outline）。本阶段着重“可用、快、低耦合”，为后续语义/编辑能力留足演化接口。

---
## 1. 范围与裁剪
Scope = { L0 项目加载, L1 Outline 抽取, L2 缓存+增量, L3 RPC/CLI 服务, L5 Prompt 窗口(Outline Only) }
Excluded = { 语义文件、语义状态机、Internal 漂移、Alias、SCC 组语义、调查任务、AST 编辑、成员级 Outline、Pack 复杂过滤、语义优先级调度 }

保留多层 Hash (structure/publicImpl/internalImpl/xmlDoc/impl/cosmetic) —— 为 Phase 2 一次性接入语义无需重算历史。

---
## 2. Phase 1 最小 Index Schema
（TypeId 生成与 Hash 输入归一化规则详见附录：[TypeId 与 Hash 规则](./Appendix_TypeId_and_HashRules_P1.md)；全量/增量重建触发与文件写入流程详见：[增量处理流程伪代码](./Appendix_Incremental_Flow_Pseudocode_P1.md)。）
示例 (`index.json` 部分)：
```jsonc
{
  "schemaVersion": "1.0-p1",
  "generatedAt": "2025-09-02T12:00:00Z",
  "projects": [ { "id": "P1", "name": "App.Core", "path": "src/App.Core/App.Core.csproj", "tfm": "net9.0", "hash": "<ProjectHash>" } ],
  "types": [
    {
      "id": "T_ab12cd34",
      "fqn": "App.Core.NodeStore",
      "projectId": "P1",
      "kind": "class",
      "files": ["src/App.Core/NodeStore.cs"],
      "structureHash": "9F2A441C",
      "publicImplHash": "11AA22BB",
      "internalImplHash": "33CC44DD",
      "cosmeticHash": "55EE66FF",
      "implHash": "5BC19F77", // H("v1|"+publicImplHash+"|"+internalImplHash)
      "xmlDocHash": "17AD90B2",
      "outlineVersion": 2,
      "depthHint": 0
    }
  ],
  "configSnapshot": {
    "hashVersion": "1",
    "structureHashIncludesXmlDoc": false,
    "includeInternalInStructureHash": false
  }
}
```
省略字段：`changeClass / internalChanged / propagatedCause / internalDriftState / lastSemanticBase / semanticState / semanticGroupId`。

---
## 3. 文件与目录结构（建议）
```
.codecortex/                (运行期缓存根)
  index.json
  types/<TypeId>.outline.md
  prompt/prompt_window.md
  logs/
  tmp/

src/
  CodeCortex.Core/
    Models/TypeHashes.cs
    Models/TypeRecord.cs
    Hashing/TypeHasher.cs
    Outline/OutlineExtractor.cs
    SymbolIndex/SymbolResolver.cs
    Incremental/ChangeDetector.cs
    Incremental/WatcherBatcher.cs
    Incremental/IncrementalProcessor.cs
    Prompt/PromptWindowBuilder.cs
    Prompt/AccessTracker.cs
    Util/AtomicIO.cs
  CodeCortex.Workspace/
    WorkspaceLoader.cs
    ProjectScanner.cs
  CodeCortex.Service/
    RpcContracts.cs
    RpcHandlers.cs
    ServerHost.cs
  CodeCortex.Cli/
    Program.cs
    Commands/*.cs

tests/
  CodeCortex.Tests/
    HashingTests.cs
    OutlineTests.cs
    SymbolResolveTests.cs
    IncrementalTests.cs
    PromptWindowTests.cs
```

---
## 4. 接口契约 (Phase 1)
符号解析匹配优先级与模糊/通配策略详见附录：[符号解析算法](./Appendix_SymbolResolveAlgorithms_P1.md)。
### 4.1 RPC 方法
| 方法 | 描述 | 失败码 |
|------|------|--------|
| ResolveSymbol(path) | 路径/后缀/通配符/模糊解析 | SymbolNotFound / AmbiguousSymbol |
| GetOutline(path|typeId) | 返回 Outline 文本（不存在则即时生成并写缓存） | SymbolNotFound |
| SearchSymbols(query,limit) | 名称/前缀/模糊混合搜索 | - |
| GetPromptWindow() | 聚合窗口 (仅 Outline) | - |
| Pin(path|id) / Unpin(path|id) | 置顶 / 取消 | SymbolNotFound |
| Dismiss(path|id) | 从 recent 临时移除 | SymbolNotFound |
| Status() | { projects, typesIndexed, watcherQueueDepth, outlineCacheHitRatio, memoryMB } | - |

### 4.2 CLI 命令
```
codecortex resolve <symbol>
codecortex outline <symbol>
codecortex search <query> [--limit 20]
codecortex window
codecortex pin <symbol>
codecortex unpin <symbol>
codecortex dismiss <symbol>
codecortex status
codecortex daemon start|stop
```

---
## 5. Outline 格式 (Phase 1 不含依赖统计裁剪)
成员签名排序与 partial 合并、嵌套/泛型 FQN 规范承接附录：[TypeId 与 Hash 规则](./Appendix_TypeId_and_HashRules_P1.md)。
```
# <FQN> <TypeId>
Kind: <kind> | Files: <rel1>[,rel2...] | Assembly: <asm> | StructureHash: <structureHash>
PublicImplHash: <publicImplHash> | InternalImplHash: <internalImplHash> | ImplHash: <implHash>
XmlDocHash: <xmlDocHash>
XMLDOC: <first line>

Public API:
  + <signature1>
  + <signature2>
```
去除：Implements/DependsOn 块（可在 Phase 2 加回）。

---
## 6. Hash 分类判定(最小实现)
各 Hash 输入的语法节点范围、去 Trivia / 注释策略、Cosmetic 采集最小算法与冲突扩展策略详见附录：[TypeId 与 Hash 规则](./Appendix_TypeId_and_HashRules_P1.md)。
| 判定 | 行为 |
|------|------|
| structureHash 变 | 重写 Outline；outlineVersion++ |
| 仅 publicImplHash/internalImplHash 变 | 重写 Outline (结构行不变)；outlineVersion++ （方便 diff）|
| 仅 xmlDocHash 变 | 重写 Outline；outlineVersion++ |
| 仅 cosmeticHash 变 | 可选：不写（默认仍写，方便外界看到“时间”差异） |

*实现简化：不做 changeClass 计算，只比较 hash 是否变化。*

---
## 7. Prompt 窗口策略 (Outline Only)
窗口预算统计单位（Phase1 = 字符数，可在 Phase2 升级为 token 估算）及降级裁剪逻辑可结合指标定义见附录：[Status 指标定义](./Appendix_Status_Metrics_Definition_P1.md)。
| 区域 | 来源 | 规则 |
|------|------|------|
| Pinned | 用户 pin 的类型 | 顺序=pin 时间；总字数占比>60%则拒绝新 pin（返回提示） |
| Focus | 最近 N (默认8) 查询 + 其直接基类/接口 | 去重后按最近访问时间降序 |
| Recent | 其他访问历史 | LRU；溢出裁剪尾部 |

降级：当剩余预算<15%时，Recent 裁剪到 0；Focus 若仍超限，去除最旧。段落不做局部裁剪。

---
## 8. 执行序列（按 AI Coder 会话 Session 划分）
底层文件监控 -> 聚合 -> Re-hash -> Index 更新的串行步骤与伪代码见附录：[增量处理流程伪代码](./Appendix_Incremental_Flow_Pseudocode_P1.md)。
| Session | 目标 | 步骤产出 | 退出标准 |
|---------|------|----------|----------|
| S1 Workspace & 扫描 | 加载解决方案, 枚举类型 | WorkspaceLoader, ProjectScanner, 初次类型列表 | 能输出类型计数 |
| S2 Hash & Outline | 计算所有 hash + 生成第一个 Outline | TypeHasher, OutlineExtractor, TypeHashes 模型 | 生成 ≥1 个 outline 文件 |
| S3 Index 构建 | 写 index.json + 读取 API | IndexWriter/Reader | 重启后 index 复用成功 |
| S4 符号解析 | 实现精确/后缀/通配符/模糊 | SymbolResolver + 测试 | 解析 4 类案例全部通过 |
| S5 Watcher & 增量 | 文件变更 -> 重新 hash -> 更新 outline | WatcherBatcher, ChangeDetector, IncrementalProcessor | 修改一个文件 <300ms 完成更新 |
| S6 Prompt 窗口 | Pinned/Focus/Recent 生成窗口文件 | PromptWindowBuilder, AccessTracker | window 文件存在且更新按预期 |
| S7 RPC 服务 | JSON-RPC Host + Handler 映射 | RpcHandlers, ServerHost | CLI 能远程调用 outline/resovle |
| S8 CLI 工具 | wrap RPC + 命令解析 | CLI Commands | 全部命令执行成功 |
| S9 测试与性能 | 补测试覆盖 & 基础性能测量 | Tests 目录 | 初次索引(≤2k types) <5s |
| S10 稳定化 | Bugfix & 日志 & 文档 | logs, README 补丁 | 所有测试通过, 无严重泄漏 |

*允许并行：S4/S5 可交错；S7 可在 S5 完成 70% 时启动。*

---
## 9. 关键内部接口 (简化签名草案)
```csharp
public record TypeHashes(string Structure, string PublicImpl, string InternalImpl, string XmlDoc, string Cosmetic, string Impl);
public record TypeRecord(
  string Id, string Fqn, string ProjectId, string Kind,
  IReadOnlyList<string> Files, TypeHashes Hashes, int OutlineVersion, int DepthHint);

public interface IWorkspaceLoader { Task<LoadedSolution> LoadAsync(string entryPath, CancellationToken ct); }
public interface ITypeEnumerator { IEnumerable<INamedTypeSymbol> Enumerate(Compilation c); }
public interface ITypeHasher { TypeHashes Compute(INamedTypeSymbol symbol, PartialFilesContext ctx, HashConfig cfg); }
public interface IOutlineExtractor { string BuildOutline(INamedTypeSymbol symbol, TypeHashes hashes, OutlineOptions opts); }
public interface ISymbolResolver { ResolveResult Resolve(string query, ResolveOptions opts); IEnumerable<SymbolCandidate> Search(string query, int limit); }
public interface IIncrementalProcessor { Task<IReadOnlyList<TypeUpdateResult>> ProcessAsync(FileChangeBatch batch, CancellationToken ct); }
public interface IPromptWindowBuilder { string Build(PromptWindowConfig cfg); }
```

---
## 10. 测试计划 (Phase 1)
| 测试组 | 重点 | 用例示例 |
|--------|------|----------|
| HashingTests | 结构 vs 实现区分 | 添加方法签名 vs 修改方法体 vs 改注释 |
| OutlineTests | partial/nested 泛型 | 两个 partial 合并；nested class；generic<T> |
| SymbolResolveTests | 查询策略 | 精确 / 后缀唯一 / 通配符 * / 模糊编辑距=1 |
| IncrementalTests | 批量变更与删除 | 修改1文件；新增文件；删除文件；重命名文件 |
| PromptWindowTests | Pinned/Focus/Recent | pin 饱和；焦点回退；预算降级 |
| CliRpcTests | 端到端 | resolve+outline+window 流程 |

---
## 11. 性能与指标基线
各指标采集方式、统计窗口及字段说明详见附录：[Status 指标定义](./Appendix_Status_Metrics_Definition_P1.md)。
| 指标 | 目标 | 说明 |
|------|------|------|
| 初次索引时间 | ≤ 5s (2k 类型) | Debug 模式可放宽 2x |
| 单文件增量 | ≤ 300ms | 不含磁盘 flush 高峰 |
| Outline 生成失败率 | 0 | 有异常 -> 记录日志并回退中止写入 |
| 内存占用 | < 600MB | 中型解决方案 |

`Status()` 暴露：`typesIndexed`, `initialIndexDurationMs`, `lastIncrementalMs`, `watcherQueueDepth`, `outlineCacheHitRatio`, `memoryMB`。

---
## 12. 日志与可观测性 (Phase 1)
日志文件的原子写入、目录结构与并发写策略见附录：[Atomic IO 与文件布局](./Appendix_AtomicIO_and_FileLayout_P1.md)。
| 日志文件 | 内容 |
|----------|------|
| logs/index_build.log | 初次与重建摘要 |
| logs/incremental.log | 每批次：文件数 / 类型数 / 耗时 |
| logs/resolve_trace.log (可选) | 模糊解析歧义样本 |
| logs/errors.log | 异常栈 / Outline 写入失败 |

---
## 13. 向后兼容 Hook（为 Phase 2 预留）
| 未来功能 | 预留点 |
|----------|------|
| 语义缓存 | 保留 TypeHashes + outlineVersion；不清理 public/internal Hash 字段 |
| Internal 漂移 | 留存 internalImplHash 字段；将来添加 internalDriftState |
| Alias | TypeId 生成稳定算法固定；未做 rename 检测但未阻碍 future alias 扫描 |
| SCC | depthHint 字段保留（将来拓扑更新时填充） |
| 编辑管线 | Outline 写入已原子化；后续 diff 合并时无需调整结构 |

---
## 14. 失败与回退策略
| 失败类型 | 策略 |
|----------|------|
| Roslyn 解析异常 | 记录错误；跳过该文件；不写破损 Outline |
| 写文件失败（IO锁） | 重试 2 次(100ms/500ms 回退)；失败记录并跳过 | 
| Watcher 风暴 | 超过 500 待处理文件 -> 触发全量重扫 fallback（记录原因） |
| 符号解析模糊过多 | 限制返回前 20 + candidates 列表；写 trace |

---
## 15. 完成判定 (Phase 1 Definition of Done)
- CLI / RPC 所列方法全部可用且稳定
- 初次索引 + 两种增量场景（添加 public 方法 / 修改内部实现）均正确更新 Outline
- Prompt 窗口展示 Pinned/Focus/Recent 三区域，预算策略有效
- 全部测试组通过；Status 指标返回正常；关键日志存在
- 文档：README 或设计文档链接说明如何启动服务与示例命令
  - 附录：上述新增附录文件均存在且被主文档引用（Hash/TypeId、符号解析、增量流程、指标、IO、测试夹具）

---
## 16. 建议首个端到端验证脚本 (概念)
1. 启动服务：`codecortex daemon start`
2. `codecortex status` → 显示 typesIndexed
3. `codecortex outline App.Core.NodeStore`
4. 修改 `NodeStore.cs` 方法体 → 等待增量（<300ms）
5. 再次 `outline` → ImplHash 变化，StructureHash 不变
6. 添加一个 public 方法 → StructureHash 变化
7. `codecortex window` → 新类型进入 Focus/Recent
8. `codecortex pin App.Core.NodeStore` → 窗口顶部固定

---
## 17. 未来升级衔接说明
Phase 2 引入：语义文件目录 `semantic/`、index 扩展字段、队列与 drift；无需变动现有 Outline 与 Hash 模块，只在增量分类时新增 changeClass 逻辑。Phase 3 后再加入 alias / SCC / 编辑。

---
(End of CodeCortex Phase 1 Guide)

---
### 附录索引（快速导航）
| 附录 | 内容概要 |
|------|----------|
| [Appendix_TypeId_and_HashRules_P1](./Appendix_TypeId_and_HashRules_P1.md) | TypeId 算法、各类 Hash 输入归一化、冲突扩展、Outline 排序规范 |
| [Appendix_SymbolResolveAlgorithms_P1](./Appendix_SymbolResolveAlgorithms_P1.md) | 精确/后缀/通配/模糊匹配算法、评分与歧义处理、返回结构 |
| [Appendix_Incremental_Flow_Pseudocode_P1](./Appendix_Incremental_Flow_Pseudocode_P1.md) | Watcher 聚合窗口、变更归并、partial 汇总、Index 更新与重建触发、失败回退 |
| [Appendix_Status_Metrics_Definition_P1](./Appendix_Status_Metrics_Definition_P1.md) | Status 指标定义、采样点、计算窗口与公式 |
| [Appendix_AtomicIO_and_FileLayout_P1](./Appendix_AtomicIO_and_FileLayout_P1.md) | 运行期目录结构、原子写协议、并发/锁策略、损坏恢复 |
| [Appendix_Test_Fixtures_Plan_P1](./Appendix_Test_Fixtures_Plan_P1.md) | 各测试组最小夹具构造策略与样例代码结构 |

---
## 开发经验与注意事项 (新增)
### 构建 / 测试工作流提醒
- 修改核心实现（解析 / 哈希 / 复用策略）后务必执行带构建的测试：`dotnet test`（不要直接 `--no-build`）。
- 仅当确认只新增/修改测试文件且核心源码未改时，才可使用 `--no-build` 加速。
- 若需要频繁局部验证某单测：`dotnet test -c Debug --filter FullyQualifiedName~SymbolResolver`。

### 符号解析实现要点快照
- 匹配顺序：Exact → ExactIgnoreCase → Suffix → Wildcard → Fuzzy。
- Fuzzy 阈值策略：len(query) ≤12 → 1；>12 → 2。
- Ambiguous 标记：基于“完整 suffix 结果”而不是截断结果，先收集再裁剪。
- 当前采用完整 DP Levenshtein 保证正确性；性能可接受，后续可换 banded 实现。

### 未来改进候选
- `resolve` / `search` 行为差异化（search 不提前短路、返回更宽集合）。
- `SymbolResolverOptions`：可配置阈值、禁用 Fuzzy、限制通配符上限。
- 解析阶段统计指标：resolveDurationMs / fuzzyCandidates / wildcardResultCount。

### 常见易错点
| 场景 | 风险 | 提示 |
|------|------|------|
| 使用 `--no-build` 运行旧二进制 | 测试未真正覆盖新改动 | 变更核心代码后先完整构建 |
| 修改 Levenshtein 早退策略 | 产生距离=2 误判 | 保留完整 DP 或添加单测覆盖边界 |
| 歧义标记基于截断集合 | 误判或不稳定 | 始终全量收集再标记再截断 |

