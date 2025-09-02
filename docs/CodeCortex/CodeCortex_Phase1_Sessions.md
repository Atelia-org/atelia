# CodeCortex Phase1 Sessions 执行计划 & 进度追踪

> 目的：将 Phase1 (S1~S10) 的目标、关键任务、产出物、引用文档、退出标准、度量与风险集中，便于每日/每次开发会话更新。文档是“活”文件；进度复选框随开发推进勾选。
>
> 关联主文档：`CodeCortex_Phase1.md` 及附录 (Hash 规则 / 符号解析 / 增量伪代码 / 指标 / AtomicIO / 测试夹具)。

## 目录
- [会话概览表](#会话概览表)
- [单会话详细计划](#单会话详细计划)
- [滚动进度与里程碑](#滚动进度与里程碑)
- [风险燃尽表](#风险燃尽表)
- [度量采集清单](#度量采集清单)

---
## 会话概览表
| Session | 目标摘要 | 关键产出 (Artifacts) | 主要参考附录 | 退出标准 (简) | 预计工时 |
|---------|----------|----------------------|--------------|--------------|----------|
| S1 Workspace & 扫描 | 解决方案加载 + 类型枚举 | `WorkspaceLoader` 初版 / 枚举日志 | Incremental Flow §1~4 | 输出类型计数日志 | 0.5d |
| S2 Hash & Outline | Hash 计算 stub + 初次 Outline | `ITypeHasher` 实现 / `IOutlineExtractor` / 1 个 outline 文件 | TypeId & Hash Rules | ≥1 类型 outline 生成 | 1d |
| S3 Index 构建 | 写入 `index.json` + 复用 | Index Writer/Reader + AtomicIO | AtomicIO & FileLayout | 重启后复用成功 | 0.5d |
| S3.5 Quick Invalidation | Timestamp 级快速失效判定 | FileManifest + ReuseDecider + 测试 | Incremental Flow 附录 §Manifest | 未变→复用 变更→重建 日志含 Files/Changed | 0.25d |
| S4 符号解析 | 精确/后缀/通配/模糊 | `SymbolResolver` + 测试 | Symbol Resolve Algorithms | 4 类查询测试过 | 0.5d |
| S5 Watcher & 增量 | 文件变更 → hash & outline 更新 | WatcherBatcher / ChangeDetector / IncrementalProcessor | Incremental Flow | 修改1文件 <300ms | 1d |
| S6 Prompt 窗口 | Outline-only Prompt Window | AccessTracker / PromptWindowBuilder | Status Metrics (预算提及) | 生成窗口文件 | 0.5d |
| S7 RPC 服务 | JSON-RPC 服务化 | ServerHost / RpcHandlers | (主文档 §4) + AtomicIO | CLI 可 RPC outline | 0.5d |
| S8 CLI 工具 | 全部命令封装 | Commands/* | 主文档 §4.2 | 全命令执行成功 | 0.5d |
| S9 测试与性能 | 覆盖 + 性能基准 | Test fixtures / Baselines | Test Fixtures Plan | 初次索引 ≤5s | 1d |
| S10 稳定化 | 清理 & 文档 & 日志 | README + 日志审核 | Status Metrics / AtomicIO | DoD 全满足 | 0.5d |

> 注：工时为相对估算（人天），具体按复杂度调整；并行可在 S4/S5 有 60% 进度时启动 S7。

---
## 单会话详细计划
### S1 Workspace & 扫描
- 目标：加载 .sln / .csproj 并枚举所有 `INamedTypeSymbol`（公共+内部）。
- 任务清单：
  - [x] `MsBuildWorkspaceLoader` 添加日志 + 失败重试 (实现：3 次尝试 + 线性退避 + Auto 模式回退 Fallback)
  - [x] 简单枚举器：`ITypeEnumerator` 接口与实现 (`RoslynTypeEnumerator` 递归命名空间/嵌套类型)
  - [x] 输出统计：项目数 / 类型数 → `index_build.log` (`Summary Projects=14 Types=240 DurationMs=8.5~11.7s`)
- 产出文件：`WorkspaceLoader.cs` (扩充), `TypeEnumerator.cs` (新)
- 引用：附录 *Incremental Flow* (初始化段) / *AtomicIO*
- 退出标准：运行命令 (临时 console) 打印 “Projects=X Types=Y”。

执行摘要：
```
Force 模式一次完整加载: Projects=14 Types=240 Duration≈11.7s
Auto 模式加载: Projects=14 Types=240 Duration≈8.6s
Fallback 模式验证（空/异常路径）: Adhoc Projects=1 Types≈272 (聚合 .cs 文件)
记录日志文件: .codecortex/logs/index_build.log (包含 Attempt/Error/Warning/ Summary 行)
```
偏差与说明：初次耗时高于 Phase1 目标基线（≤5s）——哈希/并行化与延迟引用解析优化将在 S2/S3 引入，不在 S1 优化范围。

### S2 Hash & Outline
- 任务：
  - [x] `TypeHasher`: 5 类 hash (结构哈希已加入规范排序+成员签名格式；TODO: 更精细 Trivia 归一)
  - [x] `OutlineExtractor`: 按 Phase1 格式输出（Public API 列表）
  - [x] `TypeId` 生成工具类 (含冲突日志 `hash_conflicts.log`)
  - [x] 全量 Outline 写入命令：`codecortex outline-all <sln>` 生成 `.codecortex/types/*.outline.md`
- 验证：手工 diff 一个类型的结构 vs 修改内部实现 hash 差异。
- 风险：Roslyn 语法异常。缓解：try/catch。

执行摘要：
```
新增命令: outline-all  (与 scan --gen-outlines 并存)
结构哈希：成员排序优先级=NestedType>Field>Property>Event>Method，签名去参数名，仅类型序列；可稳定重建
可测试性：引入 HasherIntermediate/IHashFunction/ITriviaStripper，测试覆盖结构稳定与实现变化
初次生成：Outlines≈(public+internal 类型计数)；日志条目 OutlineAll Projects=.. Outlines=.. DurationMs=..
```
偏差与说明：CosmeticHash 仍为粗粒度（全文拼接）。后续阶段可替换为统一格式归一。

调试开关：
```
环境变量: CODECORTEX_TYPEHASH_DEBUG=1  (仍兼容旧的 CODECORTEX_DEBUG=1)
作用: TypeHasher 在 Collect/Hash 阶段输出结构行、成员枚举及方法主体采集预览（截断）。
使用场景: 调试结构哈希不变 / 实现哈希未区分的问题；默认不设置避免噪声。
```

### S3 Index 构建
- 任务：
  - [x] `IndexModel` + `IndexStore` (Load/Save)
  - [x] 原子写 + 损坏回退 (AtomicFile.Replace + .bak 回退)
  - [x] 启动时存在 index 基础复用 (L0)
  - [x] S3.5: FileManifest 时间戳快速失效 (L1) + `IndexReuseDecider`
  - [x] 单测：复用 / 修改文件失效 / 删除文件失效
- 验证：删进程重启 outline 不重算（比较计时）。

执行摘要：
```
初次构建: Summary Projects=.. Types=.. DurationMs=..
复用判定日志: ReuseDecision OK Files=N Changed=0 或 ReuseDecision REBUILD Files=N Changed=k
CLI 输出: Index reuse (timestamp): Files=N Changed=0 Projects=.. Types=..
Manifest: 记录每个源文件 LastWriteUtcTicks (后续增量基础)
```
偏差与说明：当前仅使用时间戳；未启用内容哈希以降低 IO；后续 L2/L3 将引入差异集合与局部更新。

### S4 符号解析
- 任务：
  - [ ] 构建 nameIndex / fqnIndex / suffixTrie (可简化 List 前缀过滤起步)
  - [ ] 模糊匹配距=1；>12长度距=2
  - [ ] CLI `resolve` 输出 JSON（或 table）
- 测试：4 类逻辑 + Ambiguous/NotFound。

### S5 Watcher & 增量
- 任务：
  - [ ] FileSystemWatcher + 400ms debounce
  - [ ] Batch 处理逻辑 & 统计 lastIncrementalMs
  - [ ] 全变更分类布尔判断 + outlineVersion++
- 性能测量：修改一个文件计时 <300ms。

### S6 Prompt 窗口 (Outline Only)
- 任务：
  - [ ] AccessTracker: 最近访问 LRU
  - [ ] Pinned 列表（内存+持久化 JSON 简化可延后）
  - [ ] WindowBuilder: Pinned > Focus(最近 N=8) > Recent
  - [ ] 预算字符：默认 40k char，上溢裁剪 Recent
- 验证：调用三次 outline 触发 Recent & Focus 生成文件。

### S7 RPC 服务
- 任务：
  - [ ] JSON-RPC Host (StreamJsonRpc over local TCP 或 stdio——选一)
  - [ ] Handlers: outline / resolve / search / status
  - [ ] 简单健康日志
- 验证：CLI 使用 RPC 取得 outline。

### S8 CLI 工具
- 任务：
  - [ ] 子命令：resolve / outline / search / window / pin / unpin / dismiss / status / daemon start|stop
  - [ ] 守护进程：写 `daemon.pid`，检测已运行
- 验证：脚本执行顺序（主文档 §16）。

### S9 测试与性能
- 任务：
  - [ ] 按测试附录建立 fixtures
  - [ ] Baseline outline 文件（预期 hash）
  - [ ] 性能测量 helper + 输出日志条目
- 验证：CI / 本地运行绿。

### S10 稳定化
- 任务：
  - [ ] README / 快速开始 / 架构示意
  - [ ] 日志轮转检查（>5MB rename）
  - [ ] 内存与 hit ratio 采样 sanity
  - [ ] 清理 TODO 标记（允许小于10个残留）
- 验证：DoD 列表打勾。

---
## 滚动进度与里程碑
| Session | Planned Start | Actual Start | Planned Finish | Actual Finish | Status | Notes |
|---------|---------------|--------------|----------------|---------------|--------|-------|
| S1 | 2025-09-02 | 2025-09-02 | 2025-09-02 | 2025-09-02 | ✅ | 初次加载耗时>5s, 待后续并行/延迟符号解析优化 |
| S2 | 2025-09-02 | 2025-09-02 | 2025-09-02 | 2025-09-02 | ✅ | Hash/Outline 实现+单测修复 & 调试开关添加 |
| S3 | 2025-09-02 | 2025-09-02 | 2025-09-02 | 2025-09-02 | ✅ | Index+AtomicIO+基本复用 |
| S3.5 | 2025-09-02 | 2025-09-02 | 2025-09-02 | 2025-09-02 | ✅ | 时间戳快速失效 (FileManifest) |
| S4 | | | | | ☐ | |
| S5 | | | | | ☐ | |
| S6 | | | | | ☐ | |
| S7 | | | | | ☐ | |
| S8 | | | | | ☐ | |
| S9 | | | | | ☐ | |
| S10 | | | | | ☐ | |

Milestones:
- M1 (S1-S3) 初次索引可重启复用
- M2 (S4-S5) 增量 + 解析闭环
- M3 (S6-S7) 服务化 & Prompt
- M4 (S8-S9) 全 CLI + 测试 & 性能达标
- GA (S10) 稳定交付

---
## 风险燃尽表
| 风险 | 影响 | 当前策略 | 触发监控 | 状态 |
|------|------|----------|----------|------|
| 初次索引超时 | 延迟启动 | 分批 hash, 日志警告 | initialIndexDurationMs | 未触发 |
| 增量 >300ms | 体验下降 | Debounce + 优化 hash | lastIncrementalMs | 未触发 |
| 解析歧义多 | CLI 体验差 | 排序+提示建议 | Ambiguous 计数 | 未触发 |
| 日志过大 | IO 影响 | 5MB 轮转 | 文件大小监控 | 未触发 |
| 内存膨胀 | 稳定性 | 无缓存上限 → LRU | memoryMB | 未触发 |
| 时间戳精度碰撞 | 变更误判复用 | 后续引入可选内容哈希 | reuseMismatchCount | 未触发 |

---
## 度量采集清单
| 指标 | 采集点 | 责任组件 | 完成 |
|------|--------|----------|------|
| initialIndexDurationMs | 全量结束 | IndexBuilder | ☐ |
| lastIncrementalMs | 每批后 | IncrementalProcessor | ☐ |
| watcherQueueDepth | Status 查询 | WatcherBatcher | ☐ |
| outlineCacheHitRatio | Outline 请求 | OutlineCache | ☐ |
| memoryMB | Status 查询 | StatusProvider | ☐ |

---
## 增量 / 失效策略层级规划 (Added S3.5)
| Level | 名称 | 判定策略 | 行为 | 适用阶段 | 状态 |
|-------|------|----------|------|----------|------|
| L0 | Exists Only | 仅检测 index.json 是否存在 | 永远复用或全量重建 | S3 初始 | 归档 |
| L1 | Timestamp Quick Invalidation | FileManifest: 路径 + LastWriteUtcTicks 全匹配 | 若全部匹配→复用；任一不同→全量重建 | S3.5 | 已实现 |
| L2 | Diff Collection | 在 L1 基础收集 changedFiles 列表 | 仍执行全量，但输出差异日志指标 | 过渡到 S5 | 待定 |
| L3 | Partial Incremental | 针对 changedFiles 重新哈希受影响类型 / 更新 Index 子集 | 局部更新 manifest & outlines | S5 | 规划 |
| L4 | Optimized Incremental | 并行 + Debounce + 批量统计 (lastIncrementalMs, changedTypeCount) | 低延迟迭代 | S5+ | 规划 |
| L5 | Hybrid Hash Cache | 结构/实现哈希分层缓存 + 热类型优先 | 降低热区重建成本 | S6+ | 概念 |

说明：当前处于 L1；进入 S4 前无需更复杂策略。S5 实现 L3 时需：
1) 变更分类 (新增/修改/删除/重命名)
2) 映射类型→文件的反向索引 (可派生自 TypeEntry.Files 构建内存字典)
3) 受影响类型哈希 + Outline 局部重写 + NameMaps/FqnIndex 更新
4) Manifest 增量更新（添加/移除/更新时间戳）
5) 指标：lastIncrementalMs / changedTypeCount / reuseMismatchCount

风险缓解路径：
- 时间戳误判：添加可选 ContentHash (小文件阈值，或按概率抽样)
- 批量风暴：Debounce 聚合 <=400ms；超出上限（例如 >N 文件）回退全量
- 哈希退化：结构哈希失败 fallback 到 PublicImplHash + 文件长度


---
(End of CodeCortex Phase1 Sessions Plan)
