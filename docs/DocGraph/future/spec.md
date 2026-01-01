---
documentId: "W-0002-L3"
title: "DocGraph - Rule-Tier 规范"
version: 1.0.0-mvp
status: Normative
parentWish: "W-0002"
tier: Rule-Tier
shapeDependency: "api.md@0.1.0"
validFrom: 2025-12-31
supersedes: null
created: 2025-12-31
updated: 2025-12-31
---

# DocGraph - Rule-Tier 规范

> **30秒速读**
>
> 本规范定义 DocGraph v1.0 MVP 的确定性行为。
>
> | 维度 | MVP 行为 |
> |:-----|:---------|
> | **扫描范围** | `wishes/{active,completed,abandoned}/` |
> | **输出产物** | `wishes/index.md` 表格 + 结构化错误报告 |
> | **错误策略** | 聚合所有错误后统一报告，任何 Fatal/Error 导致非零退出码 |
> | **不支持** | 配置化扫描、自动修复、增量处理 |
>
> 👉 **快速导航**：[MVP边界](#1-mvp-实现边界) | [错误码](#4-错误处理-ssot) | [扩展点](#5-扩展点与接缝)

---

## §0 文档元声明

### §0.1 规范定位与适用范围

本文档是 DocGraph 工具的 **Rule-Tier 规范**，定义 **v1.0 MVP** 的确定性行为。

- **阅读前提**：读者应先阅读 [api.md](api.md)（Shape-Tier）了解工具能力外观
- **规范性等级**：Normative（本文档中的 MUST/SHOULD/MAY 条款具有约束力）
- **适用版本**：DocGraph v1.0.x
- **条款语言**：遵循 [spec-conventions.md](../spec-conventions.md) 中定义的规范语言（MUST/SHOULD/MAY）

### §0.2 与 Shape-Tier 的关系声明

本文档是 `api.md@0.1.0` 的**时态投影**——Shape-Tier 定义"工具能做什么"（天花板），本规范定义"当前版本做什么"（地板）。

| api.md 定义 | 本文档约束 |
|:------------|:-----------|
| `IWorkspaceScanner` 支持任意 glob | MVP 固定扫描 `wishes/{active,completed,abandoned}/` |
| `TableConfig` 支持自定义列 | MVP 硬编码 9 列 |
| `IBidirectionalChecker` 支持修复 | MVP 仅报告，不修复 |

> **约定**：Shape-Tier 中标记 `🚧 MVP: Narrowed` 的能力，在本规范中有对应的限定条款。

### §0.3 上位规则引用

本规范继承以下上位规则：
- [wish-system-rules.md](../../../wishes/specs/wish-system-rules.md) — Wish 文档格式约束
- [spec-conventions.md](../spec-conventions.md) — 规范语言约定与条款编号体系

**条款映射表**（本规范依赖的上位条款）：

| wish-system-rules 条款 | DocGraph 验证行为 | 本规范错误码 |
|:-----------------------|:------------------|:-------------|
| `[F-WISH-FRONTMATTER-REQUIRED]` | 检查 frontmatter 存在 | `DOCGRAPH_PARSE_NO_FRONTMATTER` |
| `[F-WISH-FRONTMATTER-REQUIRED-FIELDS]` | 校验必填字段存在且可解析 | `DOCGRAPH_WISH_FRONTMATTER_REQUIRED_FIELD_MISSING` |
| `[F-WISH-FILENAME-ID-MATCH]` | 校验文件名序号与 `wishId` 一致 | `DOCGRAPH_WISH_FILENAME_ID_MISMATCH` |
| `[S-WISH-STATUS-MATCH-DIR]` | 校验 `status` 与目录一致 | `DOCGRAPH_WISH_STATUS_DIR_MISMATCH` |
| `[F-WISH-TIER-PROGRESS-TABLE]` | 解析层级进度表格 | `DOCGRAPH_WISH_LAYER_PROGRESS_MISSING` / `DOCGRAPH_WISH_LAYER_PROGRESS_MALFORMED` |
| `[F-WISH-TIER-PROGRESS-LINKS]` | 校验层级进度表格中的产物链接 | `DOCGRAPH_LINK_TARGET_NOT_FOUND` / `DOCGRAPH_LINK_TARGET_OUTSIDE_WORKSPACE` |

### §0.4 术语限定

本节定义本规范特有术语，以及对 api.md 术语的窄化解释。

#### §0.4.1 本文档特有术语

| 术语 | 定义 | 与 api.md 关系 |
|:-----|:-----|:---------------|
| **Implicit-Registry** | `wishes/{active,completed,abandoned}/` 目录约定 | 是 `Workspace` 的 MVP 特化 |
| **Registry-Roots** | Registry 枚举得到的 Wish 文档集合 | 是 `Document` 的子集 |
| **Derived-View** | 可从 Registry-Roots 重建的输出产物 | 如 `wishes/index.md` |

#### §0.4.2 术语窄化（Narrowing）

api.md 中的以下术语在本规范中有更窄的解释：

| api.md 术语 | 本规范限定 |
|:------------|:-----------|
| `Workspace` | 固定为仓库根目录 |
| `Document` | 仅指 Wish 文档（具有 frontmatter + Layer Progress 表格） |
| `Link` | 仅验证文档间链接，不验证锚点（#anchor）有效性 |

---

## §1 MVP 实现边界

### §1.1 实现路线图（Roadmap）

| 能力 | v1.0 MVP | v2.0 Planned | 演进触发条件 |
|:-----|:---------|:-------------|:-------------|
| 扫描范围 | wishes/ 三目录 | 配置化 glob | 需要扫描自定义目录 |
| 表格生成 | 硬编码 9 列 | TableConfig | 需要自定义表格结构 |
| 双向链接 | 仅报告 | 自动修复 | 报告准确度达到 95%+ |
| 输出格式 | Markdown | +JSON/HTML | 需要机器可读输出 |
| 增量扫描 | 无（全量） | 文件监听 | 性能成为瓶颈 |

### §1.2 能力启用状态表

| api.md 接口 | 状态 | 引用条款 |
|:------------|:-----|:---------|
| `IDocumentParser.Parse` | ✅ Enabled | `[S-DOCGRAPH-PARSE-FRONTMATTER]` |
| `ILinkTracker.ExtractLinks` | ✅ Enabled | `[S-DOCGRAPH-LINK-EXTRACT]` |
| `IBidirectionalChecker.CheckAll` | ⚠️ Report-Only | `[S-DOCGRAPH-BIDIR-REPORT-ONLY]` |
| `IWorkspaceScanner.ScanAsync` | 🚧 Narrowed | `[S-DOCGRAPH-REGISTRY-SCOPE]` |
| `IIndexGenerator.GenerateTable` | 🚧 Narrowed | `[S-DOCGRAPH-TABLE-FIXED-COLUMNS]` |

### §1.3 延迟实现声明（Deferred）

以下 api.md 能力在 v1.0 中**故意不实现**：

| 能力 | 延迟原因 | 启用条件 |
|:-----|:---------|:---------|
| 配置文件支持 | MVP 优先级不足 | 收到 3+ 用户请求 |
| 增量扫描 | 需要索引基础设施 | 完成 Graph Cache 后 |
| 自动修复反向链接 | 需要验证报告准确度 | 报告准确度 ≥ 95% |

> **实现预留**：标记为 Deferred 的能力在代码中应预留接缝（Seam），但不暴露给用户。

---

## §2 输入域约束

### §2.1 Registry 约束（隐式目录）

- **[S-DOCGRAPH-REGISTRY-SCOPE]** MUST：扫描范围固定为以下目录：
  - `wishes/active/`
  - `wishes/completed/`
  - `wishes/abandoned/`

- **[S-DOCGRAPH-REGISTRY-MISSING-FATAL]** MUST：若上述任一目录不存在，触发 `DOCGRAPH_STRUCTURE_REGISTRY_MISSING` 错误。

- **[S-DOCGRAPH-WORKSPACE-ROOT]** MUST：Workspace 边界固定为仓库根目录（包含 `.git` 的目录）。

### §2.2 文档格式约束

- **[S-DOCGRAPH-PARSE-FRONTMATTER]** MUST：每个 Wish 文档必须具有有效的 YAML frontmatter。
  - 无 frontmatter → `DOCGRAPH_PARSE_NO_FRONTMATTER`
  - YAML 语法错误 → `DOCGRAPH_PARSE_INVALID_YAML`

- **[S-DOCGRAPH-WISH-FRONTMATTER-REQUIRED-FIELDS]** MUST：Wish 文档的 frontmatter 必须满足上位规则 `[F-WISH-FRONTMATTER-REQUIRED-FIELDS]`。
  - 任一必填字段缺失/空值/不可解析 → `DOCGRAPH_WISH_FRONTMATTER_REQUIRED_FIELD_MISSING`
  - `status` 取值不在 `{Active, Completed, Abandoned}` → `DOCGRAPH_WISH_FRONTMATTER_INVALID_STATUS`

- **[S-DOCGRAPH-WISH-FILENAME-ID-MATCH]** MUST：Wish 文件名中的 4 位序号必须与 frontmatter 中 `wishId` 的序号一致（参见上位规则 `[F-WISH-FILENAME-ID-MATCH]`）。
  - 不一致 → `DOCGRAPH_WISH_FILENAME_ID_MISMATCH`

- **[S-DOCGRAPH-WISH-STATUS-MATCH-DIR]** MUST：Wish 文档的 `status` 必须与所在目录一致（参见上位规则 `[S-WISH-STATUS-MATCH-DIR]`）。
  - 不一致 → `DOCGRAPH_WISH_STATUS_DIR_MISMATCH`

- **[S-DOCGRAPH-WISH-TIER-PROGRESS]** MUST：Wish 文档必须包含层级进度表格。
  - 定位规则：包含"层级进度"或"TIER Progress"标题后的首个表格
  - 缺失 → `DOCGRAPH_WISH_TIER_PROGRESS_MISSING`
  - 格式错误 → `DOCGRAPH_WISH_TIER_PROGRESS_MALFORMED`

### §2.3 边界条件处理

- **[S-DOCGRAPH-EMPTY-REGISTRY]** MUST：若 Registry 目录存在但为空（无 .md 文件），不视为错误，输出空表格。

- **[S-DOCGRAPH-NON-WISH-MD]** SHOULD：Registry 目录下的非 Wish 格式 .md 文件（如 README.md）应跳过并记录 Warning。

---

## §3 处理与输出规则

### §3.1 遍历规则

- **[S-DOCGRAPH-TRAVERSAL-ORDER]** MUST：遍历结果必须按文件路径字典序排序，保证幂等性。

- **[S-DOCGRAPH-TRAVERSAL-VISITED]** MUST：已访问文档必须记录，遇到循环引用时终止该路径。

### §3.2 提取规则

- **[S-DOCGRAPH-EXTRACT-FRONTMATTER]** MUST：从每个 Wish 文档提取以下 frontmatter 字段：
  - `wishId`, `title`, `status`, `owner`, `created`, `updated`
  - 字段缺失/空值/不可解析时仍可填充空字符串以继续聚合，但 MUST 记录 `DOCGRAPH_WISH_FRONTMATTER_REQUIRED_FIELD_MISSING`（参见 `[S-DOCGRAPH-WISH-FRONTMATTER-REQUIRED-FIELDS]`）

- **[S-DOCGRAPH-EXTRACT-TIER-PROGRESS]** MUST：解析层级进度表格，提取各层级状态。

#### §3.2.1 链接提取规则

- **[S-DOCGRAPH-LINK-EXTRACT]** MUST：扫描文档正文中的所有 Markdown 链接，按以下规则提取和分类：

  **可遍历边类型（MUST 提取并追踪）**：
  
  | 链接模式 | 示例 | 提取行为 |
  |:---------|:-----|:---------|
  | 文档链接 | `[text](path.md)` | 提取 `path.md` 作为目标路径 |
  | 带锚点链接 | `[text](path.md#anchor)` | 分类为 `Anchor`；`TargetPath` 取 `path.md`（去掉 fragment） |
  | 相对路径链接 | `[text](../sibling/doc.md)` | 提取并规范化路径 |

  **忽略边类型（MUST NOT 追踪）**：
  
  | 链接模式 | 示例 | 忽略原因 |
  |:---------|:-----|:---------|
  | 外部链接 | `[text](https://example.com)` | 非本地文档 |
  | 纯锚点链接 | `[text](#section)` | 同文档内链接，无遍历意义 |
  | 图片链接 | `![alt](image.png)` | 非文档引用 |
  | 非 `.md` 链接 | `[text](data.json)` | 非 Markdown 文档 |

  **锚点处理策略（可判定规则）**：
  - 对带锚点链接（形如 `[text](path.md#anchor)`）：
    - `Type` MUST be `Anchor`
    - `TargetPath` MUST be `Normalize(path.md)`（去掉 `#anchor` fragment 后再规范化）
    - `RawTarget` MUST 保留原始包含 `#anchor` 的文本
  - 对纯锚点链接（形如 `[text](#section)`）：MUST NOT 作为 `Link` 记录输出（即 `ExtractLinks` 不返回该条），且 MUST NOT 参与遍历/存在性验证

- **[S-DOCGRAPH-LINK-PATH-NORMALIZE]** MUST：链接目标路径必须经过规范化处理：
  - 消解 `.`（当前目录）和 `..`（父目录）
  - 转换为相对于 Workspace 根目录的规范路径
  - 使用正斜杠 `/` 作为路径分隔符（跨平台一致性）
  - 规范化后的路径 MUST 不含 `./` 或 `../` 前缀

- **[S-DOCGRAPH-LINK-BOUNDARY]** MUST：链接目标路径必须在 Workspace 边界内：
  - 若规范化后的路径指向 Workspace 根目录之外 → `DOCGRAPH_LINK_TARGET_OUTSIDE_WORKSPACE`
  - Workspace 边界 = 包含 `.git` 目录的仓库根目录（参见 `[S-DOCGRAPH-WORKSPACE-ROOT]`）

- **[S-DOCGRAPH-LINK-VALIDATE]** MUST：对每个提取的链接目标执行存在性检查：
  - 若目标文件不存在 → `DOCGRAPH_LINK_TARGET_NOT_FOUND`
  - 检查使用规范化后的路径
  - 错误报告 MUST 包含：源文件路径、原始链接文本、规范化后路径、行号

- **[S-DOCGRAPH-LINK-OUTPUT]** MUST：链接提取结果必须符合 `ILinkTracker.ExtractLinks` 接口返回的 `Link` 记录结构：
  - `SourcePath`：源文档相对路径
  - `TargetPath`：规范化后的目标路径
  - `RawTarget`：原始链接文本（未经处理）
  - `LineNumber`：链接所在行号（1-based）
  - `Type`：链接类型枚举（`LinkType`，取值为 `Document`/`Anchor`/`External`/`Image`）

### §3.3 输出规则

- **[S-DOCGRAPH-TABLE-FIXED-COLUMNS]** MUST：生成的索引表格固定包含以下 9 列：
  1. WishId（链接）
  2. 标题
  3. 状态
  4. 责任人
  5. Resolve-Tier
  6. Shape-Tier
  7. Rule-Tier
  8. Plan-Tier
  9. Craft-Tier

- **[S-DOCGRAPH-OUTPUT-IDEMPOTENT]** MUST：相同输入必须产生完全相同的输出（字节级幂等）。

- **[S-DOCGRAPH-OUTPUT-SORT-BY-WISHID]** MUST：索引表格行默认按 `wishId` 的稳定字符串升序排序，保证可复现。

- **[S-DOCGRAPH-OUTPUT-PATH]** MUST：输出文件路径固定为 `wishes/index.md`。

---

## §4 错误处理 SSOT

> **SSOT 声明**：本节是 DocGraph 错误定义的唯一权威来源。api.md §6 仅保留概念入口和导航链接。

### §4.1 错误分类体系

错误码采用 `DOCGRAPH_` 前缀 + 分类前缀，便于分组处理：

| 前缀 | 领域 | 示例 |
|:-----|:-----|:-----|
| `DOCGRAPH_STRUCTURE_` | 目录/文件结构 | `DOCGRAPH_STRUCTURE_REGISTRY_MISSING` |
| `DOCGRAPH_PARSE_` | 解析失败 | `DOCGRAPH_PARSE_INVALID_YAML` |
| `DOCGRAPH_LINK_` | 链接问题 | `DOCGRAPH_LINK_TARGET_NOT_FOUND` |
| `DOCGRAPH_WISH_` | Wish 专用 | `DOCGRAPH_WISH_TIER_PROGRESS_MISSING` |

### §4.1.1 严重度（Severity）

本规范将错误分为四级：

| 严重度 | 含义 | 默认运行结果 |
|:------|:-----|:-------------|
| `Warning` | 可报告但不阻止成功输出 | 继续，退出码保持为 0（除非同时存在 Error/Fatal） |
| `Error` | 必须失败（用于 CI/门禁），但仍可尽可能聚合更多错误 | 继续聚合，最终失败 |
| `Fatal` | 结构性/解析性致命错误；继续运行已无意义或会污染派生视图 | 必须失败；MVP 默认不覆写派生视图 |

### §4.2 错误码清单

| 错误码 | 严重度 | 触发条件 | 默认行为 |
|:-------|:-------|:---------|:---------|
| `DOCGRAPH_STRUCTURE_REGISTRY_MISSING` | Fatal | `wishes/{active,completed,abandoned}` 目录缺失 | Fail |
| `DOCGRAPH_STRUCTURE_WORKSPACE_ROOT_NOT_FOUND` | Fatal | 无法确定 Workspace 根目录（缺少 `.git`） | Fail |
| `DOCGRAPH_PARSE_INVALID_YAML` | Fatal | frontmatter YAML 语法错误 | Fail |
| `DOCGRAPH_PARSE_NO_FRONTMATTER` | Fatal | 文档缺少 frontmatter（违反 `[F-WISH-FRONTMATTER-REQUIRED]`） | Fail |
| `DOCGRAPH_WISH_FRONTMATTER_REQUIRED_FIELD_MISSING` | Fatal | frontmatter 缺少必填字段或字段值不可解析 | Fail |
| `DOCGRAPH_WISH_FRONTMATTER_INVALID_STATUS` | Fatal | `status` 不在 `{Active, Completed, Abandoned}` | Fail |
| `DOCGRAPH_WISH_FILENAME_ID_MISMATCH` | Fatal | 文件名序号与 `wishId` 序号不一致 | Fail |
| `DOCGRAPH_WISH_STATUS_DIR_MISMATCH` | Fatal | `status` 与所在目录不一致 | Fail |
| `DOCGRAPH_LINK_TARGET_NOT_FOUND` | Error | 链接目标文件不存在 | Fail |
| `DOCGRAPH_LINK_TARGET_OUTSIDE_WORKSPACE` | Error | 链接指向仓库外部 | Fail |
| `DOCGRAPH_LINK_CYCLE_DETECTED` | Warning | 检测到循环引用路径（已安全终止该路径） | Report + Continue |
| `DOCGRAPH_WISH_TIER_PROGRESS_MISSING` | Fatal | 缺少层级进度表格 | Fail |
| `DOCGRAPH_WISH_TIER_PROGRESS_MALFORMED` | Fatal | 层级进度表格格式错误 | Fail |

### §4.3 错误报告 Schema

```json
{
  "$schema": "http://json-schema.org/draft-07/schema#",
  "type": "object",
  "required": ["errorCode", "severity", "message", "sourcePath", "details"],
  "properties": {
    "errorCode": { 
      "type": "string", 
      "pattern": "^DOCGRAPH_[A-Z]+_[A-Z_]+$" 
    },
    "severity": {
      "type": "string",
      "enum": ["Warning", "Error", "Fatal"]
    },
    "message": { "type": "string" },
    "sourcePath": { "type": "string" },
    "lineNumber": { "type": "integer", "minimum": 1 },
    "details": { "type": "object" },
    "navigation": { "$ref": "#/definitions/navigation" }
  },
  "definitions": {
    "navigation": {
      "type": "object",
      "properties": {
        "ruleRef": { 
          "type": "string", 
          "description": "引用的规范条款 ID（可为本规范 `[S-DOCGRAPH-*]`/`[A-DOCGRAPH-*]`，或上位规范如 `[F-WISH-*]`）" 
        },
        "suggestedFix": { "type": "string" },
        "relatedDocs": { 
          "type": "array", 
          "items": { "type": "string" },
          "description": "相关文档链接，如 wishes/specs/wish-system-rules.md"
        }
      }
    }
  }
}
```

### §4.4 失败策略

- **[A-DOCGRAPH-ERROR-FAIL-ON-FATAL]** MUST：严重度为 `Fatal` 的错误必须导致运行失败。
- **[A-DOCGRAPH-ERROR-AGGREGATE-ALL]** MUST：必须聚合所有错误后统一报告，而非遇首错即停。
- **[A-DOCGRAPH-CLI-EXITCODE-NONZERO]** MUST：存在任何 Error/Fatal 级错误时，CLI 退出码必须非零。

- **[A-DOCGRAPH-CLI-EXITCODE-ZERO-ON-SUCCESS]** MUST：当不存在 `Error`/`Fatal` 错误时，CLI 退出码 MUST 为 0。

- **[S-DOCGRAPH-OUTPUT-NO-OVERWRITE-ON-FATAL]** MUST：若存在任何 `Fatal` 错误，MVP MUST NOT 覆写 `wishes/index.md`（防止污染派生视图）。

- **[S-DOCGRAPH-ERROR-REPORT-PATH]** MUST：结构化错误报告（聚合 JSON）输出路径固定为 `wishes/docgraph-report.json`。

### §4.5 恢复导航（Navigation）

错误信息的 `navigation` 字段设计原则：

- **[A-DOCGRAPH-NAV-RULEREF]** SHOULD：每个错误应引用触发它的规范条款 ID。
- **[A-DOCGRAPH-NAV-SUGGESTED-FIX]** SHOULD：提供简短的修复建议。
- **[A-DOCGRAPH-NAV-RELATED-DOCS]** MAY：列出相关文档链接。

**示例**：

```json
{
  "errorCode": "DOCGRAPH_WISH_TIER_PROGRESS_MISSING",
  "severity": "Fatal",
  "message": "文档缺少层级进度表格",
  "sourcePath": "wishes/active/wish-0002-doc-graph-tool.md",
  "details": {},
  "navigation": {
    "ruleRef": "[F-WISH-TIER-PROGRESS-TABLE]",
    "suggestedFix": "添加包含 '层级进度' 标题的表格，参考模板",
    "relatedDocs": [
      "wishes/specs/wish-system-rules.md"
    ]
  }
}
```

---

## §5 扩展点与接缝

### §5.1 配置化预留

以下硬编码值在代码中应作为常量或配置项，便于 v2.0 演进：

| 硬编码项 | 当前值 | 代码位置建议 |
|:---------|:-------|:-------------|
| Registry 目录列表 | `["active", "completed", "abandoned"]` | `DocGraphConfig.RegistryDirs` |
| 表格列定义 | 9 列固定结构 | `DocGraphConfig.TableColumns` |
| 输出文件路径 | `wishes/index.md` | `DocGraphConfig.OutputPath` |
| 错误报告路径 | `docgraph-report.json` | `DocGraphConfig.ErrorReportPath` |

### §5.2 版本演进路径

**v1.0 → v2.0 演进预期**：

- 配置文件支持：引入 `docgraph.yaml` 配置文件
- 增量扫描：基于文件时间戳或 hash 的增量处理
- 自动修复：双向链接检测后的自动补全

**接缝设计原则**：接缝在 v1.0 代码中存在但不暴露给用户，确保内部可测试性。

---

## 附录 A：条款索引

### A.1 按前缀分类

| 前缀 | 条款 ID | 简述 |
|:-----|:--------|:-----|
| **S** (Semantics) | `[S-DOCGRAPH-REGISTRY-SCOPE]` | 扫描范围固定 |
| | `[S-DOCGRAPH-REGISTRY-MISSING-FATAL]` | Registry 缺失处理 |
| | `[S-DOCGRAPH-WORKSPACE-ROOT]` | Workspace 边界 |
| | `[S-DOCGRAPH-PARSE-FRONTMATTER]` | frontmatter 必需 |
| | `[S-DOCGRAPH-WISH-TIER-PROGRESS]` | 层级进度表格必需 |
| | `[S-DOCGRAPH-EMPTY-REGISTRY]` | 空目录处理 |
| | `[S-DOCGRAPH-NON-WISH-MD]` | 非 Wish 文件处理 |
| | `[S-DOCGRAPH-TRAVERSAL-ORDER]` | 遍历排序 |
| | `[S-DOCGRAPH-TRAVERSAL-VISITED]` | 循环检测 |
| | `[S-DOCGRAPH-EXTRACT-FRONTMATTER]` | frontmatter 提取 |
| | `[S-DOCGRAPH-EXTRACT-TIER-PROGRESS]` | 层级进度提取 |
| | `[S-DOCGRAPH-LINK-EXTRACT]` | 链接提取与分类 |
| | `[S-DOCGRAPH-LINK-PATH-NORMALIZE]` | 路径规范化 |
| | `[S-DOCGRAPH-LINK-BOUNDARY]` | Workspace 边界检查 |
| | `[S-DOCGRAPH-LINK-VALIDATE]` | 链接存在性验证 |
| | `[S-DOCGRAPH-LINK-OUTPUT]` | 链接输出格式 |
| | `[S-DOCGRAPH-TABLE-FIXED-COLUMNS]` | 表格列固定 |
| | `[S-DOCGRAPH-OUTPUT-IDEMPOTENT]` | 输出幂等性 |
| | `[S-DOCGRAPH-OUTPUT-PATH]` | 输出路径固定 |
| | `[S-DOCGRAPH-BIDIR-REPORT-ONLY]` | 双向链接仅报告 |
| **A** (API) | `[A-DOCGRAPH-ERROR-FAIL-ON-FATAL]` | Fatal 错误失败 |
| | `[A-DOCGRAPH-ERROR-AGGREGATE-ALL]` | 错误聚合 |
| | `[A-DOCGRAPH-CLI-EXITCODE-NONZERO]` | 非零退出码 |
| | `[A-DOCGRAPH-NAV-RULEREF]` | 错误引用条款 |
| | `[A-DOCGRAPH-NAV-SUGGESTED-FIX]` | 修复建议 |
| | `[A-DOCGRAPH-NAV-RELATED-DOCS]` | 相关文档链接 |

### A.2 按章节分类

| 章节 | 条款数量 |
|:-----|:---------|
| §2 输入域约束 | 7 |
| §3 处理与输出规则 | 12 |
| §4 错误处理 SSOT | 6 |
| **总计** | **25** |

---

## 附录 B：变更历史

| 版本 | 日期 | 作者 | 变更说明 |
|:-----|:-----|:-----|:---------|
| 1.0.0-mvp | 2025-12-31 | Seeker + Craftsman | 初始创建，Phase 0 基础框架 |
| 1.0.1-mvp | 2026-01-01 | Seeker | 补充 `[S-DOCGRAPH-LINK-EXTRACT]` 条款，新增 §3.2.1 链接提取规则 |
