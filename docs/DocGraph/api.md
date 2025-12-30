---
documentId: "W-0002-L2"
title: "DocGraph - API 设计"
version: 0.1.0
status: Draft
parentWish: "W-0002"
layer: Shape-Layer
created: 2025-12-30
updated: 2025-12-30
---

# DocGraph - Shape-Layer API 设计

> **ParentWish**: [W-0002](../active/wish-0002-doc-graph-tool.md)
> **层级**: Shape-Layer（外观与接口设计）

本文档定义 DocGraph 的公共 API 外观，关注**用户看到什么**而非实现细节。

---

## §1 设计目标

### §1.1 核心使命

提供轻量级的 Markdown 文档元信息管理工具，支持：
- 解析 YAML frontmatter
- 追踪文档间链接
- 汇总字段生成索引表格
- 检测并修复双向链接

### §1.2 设计原则

| 原则 | 说明 |
|:-----|:-----|
| **最小惊讶** | API 行为应符合开发者直觉 |
| **渐进复杂度** | 简单场景用简单 API，复杂场景可组合 |
| **可测试性** | 核心逻辑与文件系统解耦 |
| **幂等性** | 相同输入产生相同输出 |

---

## §2 核心概念

### §2.1 术语定义

| 术语 | 定义 |
|:-----|:-----|
| **Document** | 带有 frontmatter 的 Markdown 文件 |
| **Frontmatter** | 文档头部的 YAML 元信息块（`---` 包围） |
| **Field** | frontmatter 中的键值对 |
| **Link** | 文档间的引用关系（Markdown 链接） |
| **BidirectionalLink** | 双向链接对（A→B 且 B→A） |
| **Workspace** | 扫描范围根目录 |

### §2.2 文档模型

```
Document
├── Path: string          # 相对于 Workspace 的路径
├── Frontmatter: Dict     # YAML 解析结果
├── Content: string       # 正文内容
├── OutgoingLinks: Link[] # 本文档引用的其他文档
└── IncomingLinks: Link[] # 引用本文档的其他文档
```

---

## §3 公共 API 外观

### §3.1 解析器 (Parser)
> ✅ **MVP 状态**: Enabled — 全功能支持，详见 [spec.md §1.2](spec.md#12-能力启用状态表)

```csharp
public interface IDocumentParser
{
    /// <summary>
    /// 解析单个 Markdown 文件的 frontmatter
    /// </summary>
    /// <param name="content">文件内容</param>
    /// <returns>解析结果，包含 frontmatter 字典和正文</returns>
    ParseResult Parse(string content);
    
    /// <summary>
    /// 从 frontmatter 提取指定字段
    /// </summary>
    /// <param name="frontmatter">已解析的 frontmatter</param>
    /// <param name="key">字段名</param>
    /// <returns>字段值，不存在返回 null</returns>
    string? GetField(IDictionary<string, object> frontmatter, string key);
}

public record ParseResult(
    IDictionary<string, object> Frontmatter,
    string Body,
    bool HasFrontmatter
);
```

### §3.2 链接追踪器 (LinkTracker)
> ✅ **MVP 状态**: Enabled — 全功能支持，详见 [spec.md §1.2](spec.md#12-能力启用状态表)

```csharp
public interface ILinkTracker
{
    /// <summary>
    /// 扫描文档中的所有 Markdown 链接
    /// </summary>
    /// <param name="content">文档内容</param>
    /// <param name="basePath">文档路径（用于解析相对路径）</param>
    /// <returns>链接列表</returns>
    IReadOnlyList<Link> ExtractLinks(string content, string basePath);
    
    /// <summary>
    /// 验证链接目标是否存在
    /// </summary>
    /// <param name="link">待验证链接</param>
    /// <returns>验证结果</returns>
    LinkValidation Validate(Link link);
}

public record Link(
    string SourcePath,     // 源文档路径
    string TargetPath,     // 目标文档路径（解析后的绝对路径）
    string RawTarget,      // 原始链接文本
    int LineNumber,        // 链接所在行号
    LinkType Type          // 链接类型
);

public enum LinkType
{
    Document,      // [text](path.md)
    Anchor,        // [text](path.md#anchor)
    External,      // [text](https://...)
    Image          // ![alt](path.png)
}
```

> ⚠️ **MVP 状态**: Report-Only — 仅报告缺失的反向链接，不自动修复，详见 [spec.md §1.2](spec.md#12-能力启用状态表)
### §3.3 双向链接检查器 (BidirectionalChecker)

```csharp
public interface IBidirectionalChecker
{
    /// <summary>
    /// 检查双向链接完整性
    /// </summary>
    /// <param name="documents">所有文档</param>
    /// <returns>缺失的反向链接列表</returns>
    IReadOnlyList<MissingBacklink> CheckAll(IEnumerable<Document> documents);
    
    /// <summary>
    /// 检查特定链接关系的反向链接
    /// </summary>
    /// <param name="source">源文档</param>
    /// <param name="target">目标文档</param>
    /// <param name="linkField">应建立反向链接的字段名</param>
    /// <returns>是否存在有效反向链接</returns>
    bool HasBacklink(Document source, Document target, string linkField);
}

public record MissingBacklink(
    string SourcePath,       // 建立链接的文档
    string TargetPath,       // 被链接的文档
    string ExpectedField,    // 期望的反向链接字段
    string SuggestedValue    // 建议添加的值
);
```
> 🚧 **MVP 状态**: Narrowed — v1.0 硬编码生成 `wishes/index.md`，详见 [spec.md §1.2](spec.md#12-能力启用状态表)

### §3.4 索引生成器 (IndexGenerator)

```csharp
public interface IIndexGenerator
{
    /// <summary>
    /// 根据配置生成 Markdown 索引表格
    /// </summary>
    /// <param name="documents">文档集合</param>
    /// <param name="config">表格配置</param>
    /// <returns>生成的 Markdown 表格文本</returns>
    string GenerateTable(IEnumerable<Document> documents, TableConfig config);
}

public record TableConfig(
    IReadOnlyList<ColumnDef> Columns,  // 列定义
    string? SortBy = null,              // 排序字段
    bool Ascending = true,              // 升序/降序
    Func<Document, bool>? Filter = null // 过滤条件
);

public record ColumnDef(
    string Header,          // 表头文本
    string FieldPath,       // 字段路径（支持点号分隔，如 "frontmatter.status"）
    string? DefaultValue,   // 字段不存在时的默认值
    Func<object, string>? Formatter = null // 自定义格式化
);
> 🚧 **MVP 状态**: Narrowed — v1.0 固定扫描 `wishes/{active,completed,abandoned}/`，详见 [spec.md §2.1](spec.md#21-registry-约束隐式目录)
```

### §3.5 工作区扫描器 (WorkspaceScanner)

```csharp
public interface IWorkspaceScanner
{
    /// <summary>
    /// 扫描工作区内所有 Markdown 文件
    /// </summary>
    /// <param name="rootPath">工作区根目录</param>
    /// <param name="pattern">文件匹配模式（默认 "**/*.md"）</param>
    /// <returns>文档列表</returns>
    IAsyncEnumerable<Document> ScanAsync(
        string rootPath, 
        string pattern = "**/*.md"
    );
}
```

---

## §4 CLI 命令（候选）

> **状态**: 草案，待评审

### §4.1 命令概览

| 命令 | 说明 |
|:-----|:-----|
| `docgraph scan <path>` | 扫描工作区并显示统计 |
| `docgraph links <path>` | 检查链接有效性 |
| `docgraph backlinks <path>` | 检查双向链接完整性 |
| `docgraph table <config.yaml>` | 根据配置生成索引表格 |
| `docgraph fix-backlinks <path>` | 自动补全缺失的反向链接 |

### §4.2 示例用法

```bash
# 扫描 wishes 目录
docgraph scan ./wishes

# 检查链接健康
docgraph links ./wishes --report=broken

# 生成 Wish 索引表格
docgraph table ./wishes/index-config.yaml --output=./wishes/index.md

# 检查并修复双向链接
docgraph fix-backlinks ./wishes --dry-run
```

---

## §5 配置文件格式（候选）

### §5.1 表格生成配置

```yaml
# index-config.yaml
source: "./active/*.md"
output: "./index.md"
table:
  columns:
    - header: "WishId"
      field: "frontmatter.wishId"
      link: true  # 自动生成链接
    - header: "标题"
      field: "frontmatter.title"
    - header: "状态"
      field: "frontmatter.status"
      format: "emoji"  # 预定义格式化器
    - header: "更新日期"
      field: "frontmatter.updated"
  sort: "frontmatter.wishId"
  ascending: true
```

### §5.2 双向链接规则配置

```yaml
# backlink-rules.yaml
rules:
  - source_field: "frontmatter.parentWish"
    target_field: "frontmatter.childWishes"
    relation: "parent-child"
  
  - source_pattern: "wishes/active/*.md"
    target_pattern: "wishes/specs/*.md"
    source_field: "layer_progress.L3"
    target_field: "frontmatter.parentWish"
```

---

## §6 错误处理

### §6.1 SSOT 导航

本节仅提供 Shape-Layer 的概念入口。

DocGraph 的 **错误码清单、严重度语义、退出码策略、结构化错误报告 schema** 的唯一权威来源是：

- [spec.md §4 错误处理 SSOT](spec.md#4-错误处理-ssot)

### §6.2 错误报告格式

> 说明：字段命名与细节约束以 Rule-Layer 为准，详见 [spec.md §4.3](spec.md#43-错误报告-schema)。

```json
{
  "errorCode": "DOCGRAPH_LINK_TARGET_NOT_FOUND",
  "severity": "Error",
  "message": "链接目标不存在",
  "sourcePath": "wishes/active/wish-0001.md",
  "lineNumber": 42,
  "details": {
    "rawTarget": "../specs/missing.md",
    "resolvedPath": "wishes/specs/missing.md"
  },
  "navigation": {
    "ruleRef": "[S-DOCGRAPH-LINK-EXTRACT]",
    "suggestedFix": "检查文件是否存在或路径是否正确",
    "relatedDocs": [
      "atelia/docs/DocGraph/spec.md#4-错误处理-ssot"
    ]
  }
}
```

---

## §7 待决事项 (Open Questions)

| ID | 问题 | 候选方案 | 状态 |
|:---|:-----|:---------|:-----|
| Q1 | CLI 框架选择 | System.CommandLine / Spectre.Console | ⚪ 待讨论 |
| Q2 | 配置格式 | YAML / JSON / TOML | ⚪ 待讨论 |
| Q3 | 是否支持增量扫描 | 全量扫描 / 文件监听 | ⚪ 待讨论 |
| Q4 | 输出格式 | Markdown only / +JSON / +HTML | ⚪ 待讨论 |

---

## 变更历史

| 版本 | 日期 | 作者 | 变更说明 |
|:-----|:-----|:-----|:---------|
| 0.1.0 | 2025-12-30 | DocOps | 初始草案，定义核心 API 外观 |

