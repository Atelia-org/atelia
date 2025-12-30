---
documentId: "DocGraph-ORE"
title: "DocGraph 设计原矿（ore）"
status: Ore
version: 0.1.0
created: 2025-12-30
updated: 2025-12-30
---

# DocGraph 设计原矿（ore）

> 本文档是"原矿（ore）"：用于汇总目前已发现但尚未提纯为多层级（Why-Layer/Shape-Layer/Rule-Layer/Plan-Layer/Craft-Layer）设计文档的材料。
>
> - 目标：把现有会议记录、Wish 文档、L2 API 草案中的信息收敛成一个**一致、自洽**的“素材库”。
> - 非目标：在本文中做最终决策、或替代规范性条款（normative spec）。
>
> 主要信息源：
> - W-0002 实现畅谈会记录（含 Rule-Layer 条款与 Plan-Layer 建议）：[agent-team/meeting/Meta/2025-12-30-docgraph-implementation.md](../../../agent-team/meeting/Meta/2025-12-30-docgraph-implementation.md)
> - W-0002 Wish 文档：[wishes/active/wish-0002-doc-graph-tool.md](../../../wishes/active/wish-0002-doc-graph-tool.md)
> - W-0002 Shape-Layer API 草案：[atelia/docs/DocGraph/api.md](api.md)
> - Wish 系统 Rule-Layer 规范（用于理解 `wishes/index.md` 的结构约束）：[wishes/specs/wish-system-rules.md](../../../wishes/specs/wish-system-rules.md)

---

## 0. 背景与定位（当前共识）

### 0.1 任务定位

W-0002（DocGraph 工具）的 MVP 被监护人明确裁剪为：

- 核心价值：**提取信息并汇总成表格**。
- MVP 产物：**硬编码生成 `wishes/index.md`**。
- 遍历起点：从 `wishes/` 的目录约定出发（隐式 Registry）。
- 失败策略：遇到悬空引用/结构错误应报告，默认应失败（Fail the run），而不是静默修复。

> 这意味着：DocGraph 首先是一个“派生视图生成器 + 校验器”，其次才是“图工具”。

### 0.2 命名与术语漂移（需显式管理）

当前材料存在工具命名漂移：

- 历史名：`tempsync` / “温度信息同步工具”（最初愿景偏向“同步 + 修复双向链接”）。
- 会议/决策建议名：`DocGraph`（功能性命名，强调“文档图 + 提取汇总”）。

本文统一使用 `DocGraph` 指代该工具。

---

## 1. 现实快照（Repo 状态与已暴露的问题）

> 这一节不是“要求”，而是“事实快照”，用于后续把工具做成可自举的健康检查器。

### 1.1 wishes 目录作为隐式 Registry（事实存在）

- `wishes/active/`：存在 `wish-0001-wish-system-bootstrap.md`、`wish-0002-doc-graph-tool.md`
- `wishes/completed/`、`wishes/abandoned/`：存在占位文件
- `wishes/index.md`：明确声明“派生视图 / 可重建”

### 1.2 已出现的“悬空引用 / 不一致”样例（非常适合当 MVP 测试向量）

- `wishes/index.md` 当前指向 `active/wish-0002-temperature-sync-tool.md`，但实际文件是 `wishes/active/wish-0002-doc-graph-tool.md`。
- `wishes/active/wish-0002-doc-graph-tool.md` 的 frontmatter 字段 `l2Document: "wishes/specs/api.md"`，但实际 L2 文档位于 `atelia/docs/DocGraph/api.md`，且 `wishes/specs/` 目前不存在 `api.md`。

> 以上两点，恰好覆盖 L3 条款中“悬空引用必须报告、默认失败”的路径。

---

## 2. 术语表（面向实现与文档一致性）

- **DocGraph**：从“隐式 Registry（wishes 目录约定）”出发，遍历一组已知文档（可扩展到链接可达集合），提取字段，生成表格与错误报告的工具。
- **Registry（隐式）**：MVP 中无显式配置文件；`wishes/active|completed|abandoned` 目录约定 + 目录下实际存在的文档集合，是唯一权威来源（SSOT）。
- **Root / Known Docs**：Registry 枚举得到的 Wish 文档集合（遍历起点）。
- **Dangling Reference（悬空引用）**：Markdown 链接解析后的本地目标文件不存在，或目标在 workspace 边界之外。
- **派生视图（Derived View）**：可从 SSOT 重建的输出（例如 `wishes/index.md`）。

---

## 3. MVP 规范性约束（来自会议 L3 条款的“稳定内核”）

> 这一节将会议纪要中已被写成 MUST/SHOULD/MAY 的部分提炼成“稳定内核”。
> 后续若要形成 L3 规范文档，可直接迁移/裁剪此节。

### 3.1 范围（Scope）

- DocGraph 的唯一“有实用价值”的核心能力是：**提取信息并汇总成表格**（首个硬编码目标：生成 `wishes/index.md`）。
- MVP 阶段不做任何“静默修复”（例如自动改写链接、自动迁移文件、自动补双向链接）。

### 3.2 Registry（隐式：wishes 目录约定）

- 输入集合仅来自：
  - `wishes/active/*.md`
  - `wishes/completed/*.md`
  - `wishes/abandoned/*.md`
- 不得退化为“全仓库扫描”。
- `wishes/` 或上述任一目录缺失：必须失败并报告结构错误。
- 目录枚举结果需稳定排序（字典序），保证输出可复现。
- MVP 唯一输出：生成/覆写 `wishes/index.md`。

### 3.3 图遍历（Traversal）

- 起点：仅来自 Root（隐式 Registry 目录枚举）。
- 可选扩展：对每个 root 文档解析 Markdown 链接，把“可达的本地 `.md` 文件”纳入遍历集合（形成文档图）。
- 链接边（可遍历）：
  - `[text](path.md)`
  - `[text](path.md#anchor)`（遍历目标文件时忽略 anchor）
- 忽略并不遍历：外部链接（http/https）、图片链接、非 `.md`。
- 路径处理：规范化（消解 `..`/`.`），以仓库根目录为 workspace 边界。
- 去重与终止：visited 集合去重，必须能在循环引用下终止。

### 3.4 信息提取（Extraction）

- Frontmatter：若存在文件起始 `---` YAML 块，必须尝试解析。
- YAML 解析失败：必须报错（包含文件路径与失败原因），不得以空字典替代。

**Wish 专用提取（为 `wishes/index.md` 服务，可硬编码）**：

- Wish 至少提取：`wishId`、`title`、`status`、`owner`、`updated`（来自 frontmatter）。
- Wish 的 Why-Layer 到 Craft-Layer 状态必须通过解析正文中的"层级进度 (Layer Progress)"表格得到：
  - 定位：找到包含"层级进度"标题后的首个 Markdown 表格。
  - 读取：按 `| 层级 | 状态 | ... |` 的列含义读取。
  - 映射：层级名 `Why-Layer`/`Shape-Layer`/`Rule-Layer`/`Plan-Layer`/`Craft-Layer` → 状态符号（⚪/🟡/🟢/🔴/➖）。
- 若缺少该表格、或缺少某层级行：必须报告结构错误（因为索引表格不可判定）。

### 3.5 表格生成（Table Generation）

- 输出确定性：同一输入集合与同一隐式 Registry，输出字节序列必须一致（不应引入随机性）。
- 默认排序：按 `wishId` 升序（稳定字符串排序）。

`wishes/index.md` MVP 内容规则（硬编码模板）：

- 文件头必须声明“派生视图 / 可重建”。
- 至少生成三段：Active / Completed / Abandoned。
- Active 表格固定列：`WishId`、`标题`、`Owner`、`Why-Layer`、`Shape-Layer`、`Rule-Layer`、`Plan-Layer`、`Craft-Layer`、`更新日期`。
- `WishId` 单元格生成相对链接，指向对应目录下文件。
- Quick Nav 可先生成固定空态文本，但不得崩溃。

### 3.6 错误处理（Error Handling）

- 悬空引用必须聚合报告，不得静默忽略。
- 最小错误字段：`errorCode`、`message`、`sourcePath`、`details`。
- 链接类错误：应包含 `rawTarget`、`resolvedPath`、尽可能包含 `lineNumber`。

建议错误码（MVP）：

- `WISHES_STRUCTURE_INVALID`
- `DOC_NOT_FOUND`
- `PARSE_INVALID_YAML`
- `WISH_LAYER_PROGRESS_MISSING`
- `LINK_TARGET_NOT_FOUND`
- `LINK_TARGET_OUTSIDE_WORKSPACE`

失败策略（默认）：

- 结构非法、YAML 解析失败、层级进度表不可解析：必须以失败结束。
- 任意悬空引用：建议以失败结束（便于 CI/人工流程发现）。
- 可选开关：允许“仍生成表格但保留错误报告”，但即便允许也不能吞掉错误。

---

## 4. L2 API 草案（“愿景外观”，与 MVP 的关系）

> Shape-Layer 文档在 [atelia/docs/DocGraph/api.md](api.md) 中。
> 它描述的是更宽的愿景：扫描 workspace、生成表格、双向链接检查/修复、配置化输出等。

### 4.1 Shape-Layer 中的 5 个核心接口（信息源）

- `IDocumentParser`：frontmatter 解析、字段提取。
- `ILinkTracker`：提取链接、验证链接目标。
- `IBidirectionalChecker`：检查/推断缺失的反向链接（偏“修复/补全”）。
- `IIndexGenerator`：按 `TableConfig` 生成表格。
- `IWorkspaceScanner`：扫描工作区 Markdown 文件（默认 `**/*.md`）。

### 4.2 与 MVP/Rule-Layer 的张力（需要后续明确）

- **扫描范围**：Shape-Layer 倾向 workspace 扫描；Rule-Layer 强约束为"仅 wishes 三目录 roots + 可选可达扩展"。
- **双向链接**：Shape-Layer 把"检查并修复双向链接"列为使命之一；Rule-Layer 明确 MVP 不做静默修复，且更偏向"报告/失败"。
- **配置化**：Shape-Layer 有 `TableConfig`/YAML 配置设想；Rule-Layer MVP 要求硬编码生成 `wishes/index.md`。

建议处理方式（素材层面结论）：

- 将 Shape-Layer 视为"未来扩展的外观草案（愿景）"，将 Rule-Layer 视为"MVP 的约束规范"。
- 代码结构可保留扩展点（例如接口/策略），但默认行为必须满足 Rule-Layer 的确定性与失败策略。

---

## 5. Plan-Layer 实现建议（来自会议记录的候选决策）

> 这一节是实现层的候选方案集合，不代表最终确定。

### 5.1 项目结构（候选）

建议独立项目（便于测试、打包、CLI 封装），目录参考：

- `atelia/src/DocGraph/`
  - `Registry/`：隐式 Registry（wishes 目录约定）
  - `Traversal/`：链接提取、图遍历、路径规范化
  - `Extraction/`：frontmatter 解析、Wish 专用解析（含 Layer Progress 表格解析）
  - `Generation/`：`wishes/index.md` 生成（模板 + 表格渲染）
  - `Errors/`：错误模型与错误码

### 5.2 依赖选择（候选）

- Markdown：Markdig
- YAML：YamlDotNet
- 文件系统抽象：System.IO.Abstractions（便于测试）
- glob：Microsoft.Extensions.FileSystemGlobbing（若需要）

### 5.3 遍历与错误聚合（候选）

- 遍历：BFS + visited（对循环引用终止友好）。
- 错误策略：Collect-all（聚合全部错误后统一报告），而不是遇首错即停。

---

## 6. 待决事项与风险（用于后续提纯为“决策记录”）

### 6.1 待决事项（来自 Shape-Layer Open Questions + Rule-Layer 扩展点）

- CLI 框架：System.CommandLine vs Spectre.Console（或先无依赖手写参数）。
- 配置格式：YAML/JSON/TOML（MVP 可先无配置）。
- 是否支持增量扫描：全量 vs 文件监听（MVP 全量）。
- 输出格式：仅 Markdown vs +JSON/+HTML（MVP 仅 Markdown）。
- 悬空引用处理：默认失败已倾向明确；是否提供“允许生成但失败退出码”之外的模式。

### 6.2 已识别的风险

- Wish “层级进度表格解析”属于硬编码点：对标题/列名变化敏感。
  - 缓解：封装为独立解析器，单元测试覆盖各种表格变体（对齐标记、空格、列顺序等）。
- “共享产物”的双向链接规则例外：会议纪要作为 L1 共享产物不应强制 `ParentWish`。
  - 缓解：把“必须回链”限定为“专属产物（非共享会议记录）”。

---

## 7. 可直接转化为测试向量的用例清单（MVP 很值钱）

> 用例来自仓库现状 + 条款要求，用于快速写集成测试或 golden-file 测试。

- **TV-001（结构缺失）**：`wishes/active` 缺失 → `WISHES_STRUCTURE_INVALID`。
- **TV-002（索引链接悬空）**：`wishes/index.md` 指向不存在的 wish 文件 → `LINK_TARGET_NOT_FOUND`（或更具体的索引一致性错误）。
- **TV-003（l2Document 悬空）**：`l2Document` 指向不存在文件 → `LINK_TARGET_NOT_FOUND`。
- **TV-004（YAML 解析失败）**：frontmatter 非法 → `PARSE_INVALID_YAML`。
- **TV-005（层级进度缺失）**：缺少“层级进度”表格或缺少 L3 行 → `WISH_LAYER_PROGRESS_MISSING`。
- **TV-006（越界链接）**：`[x](../../outside.md)` 解析到 workspace 外 → `LINK_TARGET_OUTSIDE_WORKSPACE`。
- **TV-007（循环引用）**：A 链 B、B 链 A → 遍历终止且不重复。

---

## 8. 后续提纯建议（从 ore → 多层级文档）

- L1 Why：监护人动机与“认知带宽/上下文精确注入”的问题陈述（已在会议纪要中）。
- L2 What：保留并重命名/对齐“DocGraph”术语，明确 MVP 只覆盖 wishes/index 生成。
- L3 Rules：把第 3 节迁移为规范性条款文档（可拆分为 registry/traversal/extraction/table/errors）。
- L4 How：将第 5、6 节的候选决策收敛成决策记录（Decision Log）。
- L5 Build：落地为 `dotnet` 工具 + xUnit 测试 + 可重复生成的 `wishes/index.md` golden output。
