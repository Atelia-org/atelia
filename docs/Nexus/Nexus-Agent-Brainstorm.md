# Nexus Agent 设想与“尾随窗口注入”方案（Brainstorm）

> 目标：打造一个为 LLM Coder 而生的本地/局域网 Agent 环境，让日志、状态与上下文以低摩擦、低干扰的方式实时服务于推理与行动。

## 愿景与设计原则
- 面向 LLM 的“上下文操作系统”：最少的工具调用、最直接的高价值上下文。
- 实时，但不打扰：用户/测试输出默认清爽，Agent 自行尾随日志“静默供给”。
- 可解释性优先：每次注入都可追溯原始来源（文件、时间、类别、指纹）。
- 预算优先：在固定 token 预算内最大化“信噪比”。
- 渐进增强：MVP 从本地文件尾随开始，逐步增加采样、压缩与语义能力。

## 场景与用户价值
- 构建/测试/服务日志的“最近证据”自动注入，免去“再跑一遍”。
- 出错现场“直接看得到”：异常堆栈、失败测试摘要、最近变更摘要立即可见。
- 长流程的连续性：可“置顶（Pin）”关键片段，确保跨多轮对话不丢线索。

---

## 一、尾随窗口注入（Tailing Window Injection）

### 1.1 数据源
- DebugUtil 文件日志（默认 .codecortex/logs/{category}.log；回退 gitignore/debug-logs）。
- 构建/测试输出的镜像落盘（可选）。
- 进程型服务的标准日志（可选：通过代理转储至上述目录）。

### 1.2 触发时机（事件驱动 + 节流）
- 用户新消息到达后（必触发一次采样）。
- 工具调用完成后（成功/失败均触发）。
- 文件写入/变更事件触达（可选，按类别限流）。
- 定时心跳（可选，低频）。

### 1.3 采样窗口策略（可组合）
- 时间窗：最近 T 秒（如 10–30s）。
- 行数窗：每类最近 N 行（如 200 行）。
- 体积窗：总字节上限（如 64–256 KB）。
- 类别过滤：支持通配/白名单/黑名单（与 ATELIA_DEBUG_CATEGORIES 独立）。

### 1.4 片段处理
- 去噪：丢弃心跳、空行、重复行、低价值噪声前缀。
- 去重：同一哈希/时间戳附近的重复消息合并计数（xN）。
- 分组：按类别/会话/任务分组；按时间序聚合成“块”。
- 压缩：长栈/长 JSON 可摘要化（保留首尾 + 关键键值）。
- 高亮：异常/警告/失败测试/性能红线自动标注。

### 1.5 注入格式（建议）
- Envelope 元数据：source, category, path, time-range, hash, bytes, lines。
- 内容块：按块（chunk）顺序，保留原始顺序与时间戳，允许折叠。

示例（JSON，精简）：
```json
{
  "type": "live-log",
  "source": ".codecortex/logs",
  "window": {"seconds": 20, "maxBytes": 131072},
  "chunks": [{"category": "Incremental", "from":"15:20:01.123", "to":"15:20:12.456", "lines": 87, "hash": "..."}]
}
```

### 1.6 Pin（置顶）机制
- 触发：命令（nexus pin）、特殊标记（如日志含 [PIN]）、或 API。
- 存储：持久化到 .codecortex/pinned/ 或 gitignore/pinned/（按会话/任务键分桶）。
- 注入：优先注入，直到显式 unpin 或命中过期策略。

---

## 二、与 DebugUtil 的约定与集成

### 2.1 路径与回退
- 默认：.codecortex/logs（优先，适配 Agent 实时尾随）。
- 回退：gitignore/debug-logs → 当前目录（容错）。

### 2.2 打印与写文件策略
- 文件：始终写入（事后可查，Agent 可读）。
- 控制台：由 ATELIA_DEBUG_CATEGORIES 控制（默认不扰动测试输出）。

### 2.3 类别设计建议
- 细粒度但不过度：如 Service, Incremental, Resolver, Storage, Cli, Test。
- 允许临时类别（如 Experiment_XXXX），利于定点诊断。

### 2.4 结构化输出建议（可选）
- 约定可被简单正则识别的键值对，如 key=value；或 JSON 一行（小心体积）。
- 重要计数/耗时统一前缀（metrics: key=val unit=ms）。

### 2.5 Pinned 约定（可选，仅文本）
- 任何一行含 [PIN] 视为可 Pin 候选；后续由 Nexus 端提供“匹配策略”。

---

## 三、配置与 API 草案

### 3.1 环境变量（建议）
- ATELIA_DEBUG_CATEGORIES：控制控制台打印类别（文件始终写）。
- ATELIA_DEBUG_DIR：显式覆盖日志目录（可指向 RAM 磁盘或容器卷）。
- NEXUS_TAIL_ROOT：强制 Nexus 读取的根目录（默认跟随 DebugUtil）。
- NEXUS_TAIL_WINDOW_SEC / NEXUS_TAIL_LINES / NEXUS_TAIL_MAXBYTES：窗口控制。
- NEXUS_TAIL_INCLUDE / NEXUS_TAIL_EXCLUDE：类别过滤（逗号/分号）。

示例（PowerShell）：
```powershell
$env:ATELIA_DEBUG_CATEGORIES = "Incremental;Service"
$env:NEXUS_TAIL_WINDOW_SEC = "20"
$env:NEXUS_TAIL_INCLUDE = "Incremental;Resolver"
```

### 3.2 CLI 草案
- nexus status：显示当前尾随配置、各类别游标、最近注入指标。
- nexus tail --once/--live：拉取最近窗口并打印或输出为 JSON。
- nexus pin/unpin --category X --match "[PIN]"：管理置顶条目。
- nexus export --since 10m：导出最近窗口为压缩包或 Markdown 摘要。

### 3.3 本地 HTTP API（可选）
- GET /tail?sec=20&include=Incremental：返回注入 envelope JSON。
- POST /pin {category, pattern, ttl}；POST /unpin {id}。
- GET /status：窗口、预算、错误与指标。

---

## 四、上下文预算与注入策略

### 4.1 预算管理
- 总预算拆分：对话上下文（prompt）/工具结果/实时日志（live）三分。
- 读写自适应：当工具调用结果很大时，压缩 live 窗口；反之扩大。
- 分层注入：先结构化摘要（统计/异常概览）→ 再少量原文 → 再扩展链接。

### 4.2 重要性评估（可扩展）
- 关键词/等级：ERROR/FAIL/EXCEPTION/WARN 优先。
- 模式识别：测试失败块、堆栈、Roslyn 诊断、编译缓存统计。
- 溯源强链接：能够直接指向源文件/类别/时间，便于后续精确拉取。

### 4.3 衰减与滚动
- 时间衰减：越新的块权重越高。
- 相关性：与当前任务（路径/类别）相关性越高，越优先。
- Pin 抑制衰减：置顶片段不受滚动淘汰（或采用更长 TTL）。

---

## 五、可观测性与安全

### 5.1 指标
- 注入成功率、平均延迟（ms）、丢弃字节/行数、类别占比、重复压缩率。
- 解析错误/尾随错误计数；文件轮转次数；Pin 存活数与命中数。

### 5.2 风险与边界
- 隐私：本地日志可能含敏感信息；提供“脱敏规则/屏蔽类别”。
- 体积：长时间运行的日志无限增长；提供轮转与限额（按大小/天）。
- 并发：多 Agent/多会话尾随同一目录；需文件锁或只读策略。

---

## 六、路线图（建议）

### 6.1 MVP（P0）
- 读取 .codecortex/logs 下各类别日志；按“最近 N 秒 + 每类 N 行 + 总上限”采样。
- 注入 envelope + 原文块；可选启用去重与异常高亮。
- 基本 CLI：nexus tail --once；nexus status。

### 6.2 P1
- Pin 管理与持久化；[PIN] 约定；按任务/会话维度隔离。
- 配置：环境变量与本地 config 文件（YAML/JSON）。
- 简单规则引擎：失败/异常优先；类别白/黑名单；去噪规则。

### 6.3 P2
- 本地 HTTP API；与编辑器/前端联动的“Live 窗口”。
- 轮转与限额；导出与分享；更丰富的指标。
- 语义摘要与压缩（可选：LLM/本地 Embedding）。

---

## 七、开放问题
- 多项目/多仓库：如何跨仓库聚合？是否需要“源标识”分桶？
- 会话隔离：同一 Agent 控多个会话，如何定向注入？
- 可靠性：文件轮转/写入中断时的游标恢复策略？
- 协作：多人协同时的 Pin 权限与冲突解决？
- 扩展：是否支持事件总线（如文件系统以外的系统事件源）？

---

## 附：与当前实现的对齐点
- DebugUtil：始终写文件，按类别控制台打印；默认目录 .codecortex/logs 回退 gitignore/debug-logs。
- Incremental/Service：已有“编译缓存/负向查找缓存/统计日志”的输出，适合作为 MVP 的高价值注入源。
- 文档建议：在 AGENTS.md 中持续明确日志路径与类别最佳实践，便于新会话快速对接。

