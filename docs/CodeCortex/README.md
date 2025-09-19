# CodeCortex

> CodeCortex = 本仓库内为 AI Coder 提供的本地 .NET 结构/增量服务层（“离线语义 IDE” 基础）。当前处于 **Phase 1 (L0+L1+L2+L3+L5 Outline Only)** 实施阶段：聚焦项目加载、类型 Outline 抽取、哈希增量索引、基础 RPC/CLI、Prompt 窗口（仅 Outline）。

---
## 1. 为什么需要 CodeCortex
LLM 在大中型 .NET 代码库中缺乏：
1. 可增量维护的结构快照 (避免全量上下文爆炸)
2. 稳定可引用的类型标识 (TypeId)
3. 低噪声/分类化的变更感知 (结构 vs 实现 vs 文档 vs Cosmetic)
4. 统一 CLI / RPC 服务入口（IDE 独立）
5. Prompt 窗口聚合+Pinned/Focus/Recent 策略

CodeCortex 旨在以文件可读的缓存 + 可预测的增量管线，降低“重新理解仓库”成本，并为后续语义生成 / AST 编辑提供稳定接口。

---
## 2. 当前范围 (Phase 1)
Scope = { 项目加载 (L0), Outline 抽取 (L1), Hash+增量缓存 (L2), RPC/CLI (L3), Prompt 窗口 Outline Only (L5) }
Excluded = { 语义生成 / drift 状态机 / Alias / SCC / 调查任务 / AST 编辑 / 成员级 Outline / 依赖统计裁剪 }

完成判定 (DoD 摘要):
- 所有列出的 CLI/RPC 方法可用且稳定
- 初次索引 + 增量 (添加 public 方法、修改内部实现) Outline 正确更新
- Prompt 窗口展示 Pinned/Focus/Recent & 预算策略生效
- 测试组（Hashing/Outline/Resolve/Incremental/Prompt/CliRpc）通过
- Status 指标返回完整；日志文件存在

详见: `CodeCortex_Phase1.md` 第 15 节

---
## 3. 总体架构分层
| 层 | Phase1 实体 | 说明 |
|----|-------------|------|
| Workspace (L0) | WorkspaceLoader / TypeEnumerator | 解析 sln / csproj, 枚举类型 |
| Outline (L1) | TypeHasher / OutlineExtractor | 生成 per-type outline markdown |
| Incremental (L2) | WatcherBatcher / ChangeDetector / IncrementalProcessor | Hash 分类 & Outline 重写 / Index 更新 |
| Service (L3) | RpcHandlers / ServerHost | JSON-RPC (outline/resolve/search/status) |
| Prompt (L5 subset) | AccessTracker / PromptWindowBuilder | Pinned/Focus/Recent 聚合 (Outline only) |

未来扩展（Phase2+）：语义缓存、Alias/SCC、Pack、编辑管线等（参阅 `CodeCortex_Design.md`）。

---
## 4. 目录与缓存布局 (建议)
```
src/
  CodeCortex.Core/
  CodeCortex.Workspace/
  CodeCortex.Service/
  CodeCortex.Cli/
  ...
.codecortex/               # 运行期缓存 (可配置)
  index.json
  types/<TypeId>.outline.md
  prompt/prompt_window.md
  logs/*.log
  tmp/
```
详见附录：`Appendix_AtomicIO_and_FileLayout_P1.md`。

---
## 5. 关键数据与算法
| 主题 | 摘要 | 详细文档 |
|------|------|----------|
| TypeId | SHA256(FQN|Kind|Arity) → Base32 截断 | `Appendix_TypeId_and_HashRules_P1.md` |
| Hash 分类 | structure / publicImpl / internalImpl / xmlDoc / cosmetic / impl | 同上 |
| Outline 格式 | 标头 + Hash 行 + Public API 列表 | Phase1 主文档 §5 |
| 增量处理 | 400ms Debounce → 批处理 → 对比 Hash → outlineVersion++ | `Appendix_Incremental_Flow_Pseudocode_P1.md` |
| 符号解析 | 精确 > 后缀唯一 > 通配 > 模糊 (DL 距阈值) | `Appendix_SymbolResolveAlgorithms_P1.md` |
| 指标采集 | initialIndexDurationMs / lastIncrementalMs 等 | `Appendix_Status_Metrics_Definition_P1.md` |
| 原子写 & 恢复 | 临时文件 + File.Replace / 损坏重建 | `Appendix_AtomicIO_and_FileLayout_P1.md` |
| 测试夹具 | Fixtures & Baseline 组织 | `Appendix_Test_Fixtures_Plan_P1.md` |

---
## 6. 会话执行计划 & 路线
开发按会话 (S1~S10) 推进：见 `CodeCortex_Phase1_Sessions.md`。
Milestones: M1(索引) → M2(增量+解析) → M3(服务+Prompt) → M4(测试+性能) → GA(稳定化)。

---
## 7. 快速开始 (未来完成后示例)
```bash
# 1. 启动守护进程
codecortex daemon start --solution ./Atelia.sln
# 2. 查看状态
codecortex status
# 3. 获取 Outline
codecortex outline MemoTree.Core.NodeStore
# 4. 固定类型并查看窗口
codecortex pin MemoTree.Core.NodeStore
codecortex window
```
当前阶段：CLI 逐步实现中，可能仅提供 outline stub。

---
## 8. 贡献约定
- 代码风格：遵循 `docs/Atelia_Naming_Convention.md` 与 repository `Directory.Build.props` 规则。
- 新增公共接口需在 Phase1 附录中引用（避免多处重复定义）。
- 日志事件格式：`<UTC ISO8601> <LEVEL> <Event> <K1=V1 ...>`。
- 提交信息建议前缀：`Sx:` 对应会话 (例 `S2: implement TypeHasher structure hash`).

---
## 9. 风险摘要 (Phase1)
| 风险 | 缓解 |
|------|------|
| 初次索引慢 | 哈希并行（后续）+ 日志告警 + 结构缓存保留 |
| 增量抖动 | Debounce 400ms + Storm fallback 全量重扫 |
| 解析歧义 | 距离阈值 + 候选列表 + 后缀唯一先行 |
| 日志膨胀 | 5MB 轮转策略 |
| Cosmetic 噪声 | 可配置 skipCosmeticRewrite |

---
## 10. Roadmap (摘要)
Phase2: 语义缓存(semantic.md) / Drift 分类 / Alias / SCC 组级失效
Phase3: Pack / 依赖裁剪 / 上下游传播优先级队列
Phase4: AST 安全编辑 (rename/extract) + Dry-run 编译
Phase5: 调查任务 & 复杂查询 (Investigate)

详见：`CodeCortex_Design.md`。

---
## 11. 术语速览
| 术语 | 含义 |
|------|------|
| TypeId | 稳定类型内部 ID |
| Outline | 类型结构摘要 markdown |
| ImplHash | 公共+内部实现聚合 hash |
| Prompt Window | 合并上下文文本窗口 (Pinned/Focus/Recent) |
| structureHash | 公开结构签名集合 hash |

更多：设计文档术语章节。

---
## 12. 文档索引
| 文档 | 作用 |
|------|------|
| `CodeCortex_Phase1.md` | Phase1 规范主文档 |
| `CodeCortex_Phase1_Sessions.md` | 会话执行与追踪 |
| `CodeCortex_Design.md` | 顶层长线设计 (0.5) |
| `Appendix_TypeId_and_HashRules_P1.md` | TypeId & Hash 规范 |
| `Appendix_SymbolResolveAlgorithms_P1.md` | 解析算法 |
| `Appendix_Incremental_Flow_Pseudocode_P1.md` | 增量流程伪代码 |
| `Appendix_Status_Metrics_Definition_P1.md` | 指标定义 |
| `Appendix_AtomicIO_and_FileLayout_P1.md` | 原子 IO & 布局 |
| `Appendix_Test_Fixtures_Plan_P1.md` | 测试夹具计划 |

---
(End of README)
