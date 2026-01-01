---
docId: "W-0002-api"
title: "DocGraph v0.1 API 设计文档"
produce_by:
  - "wishes/active/wish-0002-doc-graph-tool.md"
glossary:
  - Document-Node: "文档图中的节点，表示一个文档"
  - Document-Graph: "完整的文档关系图"
  - Validation-Result: "文档关系验证结果"
  - IDocument-Graph-Visitor: "文档图访问者接口，用于生成汇总文档"
---

# DocGraph v0.1 - API 设计文档

> **版本**：v0.1.0  
> **状态**：已实现  
> **目的**：定义v0.1简化版MVP的接口和数据模型

---

## 1. 设计原则

### 1.1 简化优先
- **接口数量**：最小集，聚焦核心功能
- **数据模型**：简化结构，避免过度抽象
- **扩展性**：为v1.0预留演进空间，但不增加v0.1复杂度

### 1.2 代码驱动
- **配置 vs 代码**：v0.1采用代码驱动，Visitor模式直接实现业务逻辑
- **渐进抽象**：先实现具体需求，发现模式后再提炼通用功能

### 1.3 可测试性
- **纯函数**：核心逻辑无副作用
- **依赖注入**：支持测试替身
- **确定性**：相同输入确定输出

---

## 2. 核心数据模型

### 2.1 文档节点 (DocumentNode)

```csharp
/// <summary>
/// 文档图中的节点，表示一个文档
/// </summary>
public class DocumentNode {
    /// <summary>
    /// 文件路径（workspace相对路径，使用'/'分隔符）
    /// </summary>
    public string FilePath { get; }
    
    /// <summary>
    /// 文档标识
    /// - Wish文档：从文件名推导（wish-0001.md → W-0001）
    /// - 产物文档：frontmatter中显式声明
    /// </summary>
    public string DocId { get; }
    
    /// <summary>
    /// 文档标题（必填字段）
    /// </summary>
    public string Title { get; }
    
    /// <summary>
    /// 文档状态
    /// - Wish文档：从文件夹推导（active/ → "active", completed/ → "completed"）
    /// - 产物文档：不适用
    /// </summary>
    public string? Status { get; }
    
    /// <summary>
    /// 文档frontmatter（原始YAML解析结果）
    /// 采用开放schema模式：核心字段严格验证，扩展字段自由使用
    /// </summary>
    public IReadOnlyDictionary<string, object> Frontmatter { get; }
    
    /// <summary>
    /// 出边关系：本文档产生的文档列表
    /// 仅Wish文档有此关系
    /// </summary>
    public IReadOnlyList<DocumentNode> Produces { get; }
    
    /// <summary>
    /// 入边关系：产生本文档的Wish文档列表
    /// 仅产物文档有此关系
    /// </summary>
    public IReadOnlyList<DocumentNode> ProducedBy { get; }
}
```

### 2.2 文档图 (DocumentGraph)

```csharp
/// <summary>
/// 完整的文档关系图
/// </summary>
public class DocumentGraph {
    /// <summary>
    /// Root nodes：所有Wish文档
    /// </summary>
    public IReadOnlyList<DocumentNode> RootNodes { get; }
    
    /// <summary>
    /// 所有文档节点（包括Wish和产物文档）
    /// 按FilePath字典序排序，确保遍历确定性
    /// </summary>
    public IReadOnlyList<DocumentNode> AllNodes { get; }
    
    /// <summary>
    /// 路径索引：快速查找文档节点
    /// </summary>
    public IReadOnlyDictionary<string, DocumentNode> ByPath { get; }
    
    /// <summary>
    /// 便利方法：遍历所有文档节点
    /// </summary>
    public void ForEachDocument(Action<DocumentNode> visitor) {
        foreach (var node in AllNodes) {
            visitor(node);
        }
    }
}
```

### 2.3 验证结果 (ValidationResult)

```csharp
/// <summary>
/// 文档关系验证结果
/// </summary>
public class ValidationResult {
    /// <summary>
    /// 扫描统计
    /// </summary>
    public ScanStatistics Statistics { get; }
    
    /// <summary>
    /// 验证问题列表（按严重度排序）
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }
    
    /// <summary>
    /// 是否通过验证（无Error级别问题）
    /// </summary>
    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>
/// 扫描统计信息
/// </summary>
public class ScanStatistics {
    public int TotalFiles { get; }
    public int WishDocuments { get; }
    public int ProductDocuments { get; }
    public int TotalRelations { get; }
    public TimeSpan ElapsedTime { get; }
}

/// <summary>
/// 验证问题
/// </summary>
public class ValidationIssue {
    public IssueSeverity Severity { get; }
    public string ErrorCode { get; }
    public string Message { get; }
    public string FilePath { get; }
    public int? LineNumber { get; }
    public int? ColumnNumber { get; }
    public string? CodeSnippet { get; }
    
    // 三层建议结构
    public string QuickSuggestion { get; }      // 5秒能理解
    public string DetailedSuggestion { get; }   // 30秒能修复
    public string? ReferenceUrl { get; }        // 按需深入
}

/// <summary>
/// 修复选项
/// </summary>
public class FixOptions {
    /// <summary>
    /// 是否启用修复模式
    /// </summary>
    public bool Enabled { get; set; }
    
    /// <summary>
    /// 是否只预览不执行（dry-run）
    /// </summary>
    public bool DryRun { get; set; }
    
    /// <summary>
    /// 是否自动确认（跳过用户确认）
    /// </summary>
    public bool AutoConfirm { get; set; }
    
    /// <summary>
    /// 修复范围（v0.1仅支持CreateMissing）
    /// </summary>
    public FixScope Scope { get; set; } = FixScope.CreateMissing;
}

/// <summary>
/// 修复范围
/// </summary>
public enum FixScope {
    /// <summary>
    /// 创建缺失的文件（v0.1支持）
    /// </summary>
    CreateMissing,
    
    /// <summary>
    /// 注入frontmatter到现有文件（v1.0规划）
    /// </summary>
    InjectFrontmatter,
    
    /// <summary>
    /// 修复链接关系（v1.0规划）
    /// </summary>
    RepairLinks,
    
    /// <summary>
    /// 所有修复类型
    /// </summary>
    All
}

/// <summary>
/// 修复操作结果
/// </summary>
public class FixResult {
    public bool Success { get; }
    public string? ErrorMessage { get; }
    public string? TargetPath { get; }
    public FixActionType ActionType { get; }
    
    public static FixResult Success(string targetPath, FixActionType actionType) 
        => new FixResult { Success = true, TargetPath = targetPath, ActionType = actionType };
    
    public static FixResult Failure(string errorMessage, string? targetPath = null) 
        => new FixResult { Success = false, ErrorMessage = errorMessage, TargetPath = targetPath };
}

/// <summary>
/// 修复操作类型
/// </summary>
public enum FixActionType {
    CreateFile,
    UpdateFrontmatter,
    RepairLink
}

/// <summary>
/// 问题严重度
/// </summary>
public enum IssueSeverity {
    Info,      // 🔵 [FYI] 信息性提示
    Warning,   // 🟡 [SHOULD FIX] 建议修复
    Error,     // 🔴 [MUST FIX] 必须修复
    Fatal      // ❌ [FATAL] 致命错误，无法继续
}
```

---

## 3. 核心接口设计

### 3.1 文档图构建器 (IDocumentGraphBuilder)

```csharp
/// <summary>
/// 构建文档关系图
/// </summary>
public interface IDocumentGraphBuilder {
    /// <summary>
    /// 扫描指定目录，构建文档图
    /// </summary>
    /// <param name="wishDirectories">Wish目录列表（默认：["wishes/active", "wishes/completed"]）</param>
    /// <returns>完整的文档关系图</returns>
    DocumentGraph Build(IEnumerable<string>? wishDirectories = null);
    
    /// <summary>
    /// 验证文档关系完整性
    /// </summary>
    /// <param name="graph">要验证的文档图</param>
    /// <param name="fixOptions">修复选项（可选）</param>
    /// <returns>验证结果</returns>
    ValidationResult Validate(DocumentGraph graph, FixOptions? fixOptions = null);
}
```

### 3.2 Visitor接口 (IDocumentGraphVisitor)

```csharp
/// <summary>
/// 文档图访问者，用于生成汇总文档
/// </summary>
public interface IDocumentGraphVisitor {
    /// <summary>
    /// Visitor名称（用于输出文件命名）
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// 输出文件路径（相对workspace）
    /// 默认：{Name}.gen.md
    /// </summary>
    string OutputPath { get; }
    
    /// <summary>
    /// 依赖的frontmatter字段列表（用于自文档化和编译期检查）
    /// 示例：["defines", "issues"]
    /// </summary>
    IReadOnlyList<string> RequiredFields { get; }
    
    /// <summary>
    /// 生成汇总文档
    /// </summary>
    /// <param name="graph">完整的文档图</param>
    /// <returns>生成的文档内容</returns>
    string Generate(DocumentGraph graph);
}
```

### 3.3 已知扩展字段约定

```csharp
// 在独立文档或Attribute中声明已知扩展字段
public static class KnownFrontmatterFields {
    // 术语定义字段
    public const string Defines = "defines";
    
    // 问题跟踪字段
    public const string Issues = "issues";
    
    // 字段格式约定
    public static class Formats {
        // defines字段格式：数组，每个元素包含term和definition
        public static readonly (string Term, string Definition)[] DefinesFormat = Array.Empty<(string, string)>();
        
        // issues字段格式：数组，每个元素包含description、status、assignee等
        public static readonly (string Description, string Status, string? Assignee)[] IssuesFormat = Array.Empty<(string, string, string?)>();
    }
}
```

---

## 4. 内置Visitor实现

### 4.1 术语表生成器 (GlossaryVisitor)

```csharp
/// <summary>
/// 术语表生成器：从defines字段生成紧凑Markdown列表
/// </summary>
[FrontmatterFields(KnownFrontmatterFields.Defines)]
public class GlossaryVisitor : IDocumentGraphVisitor {
    public string Name => "glossary";
    public string OutputPath => "docs/glossary.gen.md";
    public IReadOnlyList<string> RequiredFields => new[] { KnownFrontmatterFields.Defines };
    
    public string Generate(DocumentGraph graph) {
        var builder = new StringBuilder();
        builder.AppendLine("<!-- 本文档由DocGraph工具自动生成，手动编辑无效 -->");
        builder.AppendLine($"<!-- 生成时间：{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC -->");
        builder.AppendLine($"<!-- 再生成命令：docgraph generate glossary -->");
        builder.AppendLine();
        builder.AppendLine("# 术语表");
        builder.AppendLine();
        
        // 按文档分组，生成紧凑列表
        var termsByDoc = new Dictionary<string, List<(string Term, string Definition)>>();
        
        graph.ForEachDocument(node => {
            if (node.Frontmatter.TryGetValue(KnownFrontmatterFields.Defines, out var definesObj) &&
                definesObj is IEnumerable<object> defines) {
                var terms = ExtractTerms(defines);
                if (terms.Any()) {
                    termsByDoc[node.FilePath] = terms;
                }
            }
        });
        
        // 按文档路径排序输出
        foreach (var (docPath, terms) in termsByDoc.OrderBy(kv => kv.Key)) {
            builder.AppendLine($"## {Path.GetFileName(docPath)}");
            builder.AppendLine();
            
            foreach (var (term, definition) in terms.OrderBy(t => t.Term)) {
                builder.AppendLine($"- **{term}**：{definition}");
            }
            
            builder.AppendLine();
        }
        
        return builder.ToString();
    }
    
    private List<(string Term, string Definition)> ExtractTerms(IEnumerable<object> defines) {
        // 提取术语定义的具体实现
        var terms = new List<(string, string)>();
        // ... 实现细节
        return terms;
    }
}
```

### 4.2 问题汇总器 (IssueAggregator)

```csharp
/// <summary>
/// 问题汇总器：从issues字段生成分类表格
/// </summary>
[FrontmatterFields(KnownFrontmatterFields.Issues)]
public class IssueAggregator : IDocumentGraphVisitor {
    public string Name => "issues";
    public string OutputPath => "docs/issues.gen.md";
    public IReadOnlyList<string> RequiredFields => new[] { KnownFrontmatterFields.Issues };
    
    public string Generate(DocumentGraph graph) {
        var builder = new StringBuilder();
        builder.AppendLine("<!-- 本文档由DocGraph工具自动生成，手动编辑无效 -->");
        builder.AppendLine($"<!-- 生成时间：{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC -->");
        builder.AppendLine($"<!-- 再生成命令：docgraph generate issues -->");
        builder.AppendLine();
        builder.AppendLine("# 问题汇总");
        builder.AppendLine();
        
        // 收集所有问题
        var allIssues = new List<Issue>();
        
        graph.ForEachDocument(node => {
            if (node.Frontmatter.TryGetValue(KnownFrontmatterFields.Issues, out var issuesObj) &&
                issuesObj is IEnumerable<object> issues) {
                var docIssues = ExtractIssues(node, issues);
                allIssues.AddRange(docIssues);
            }
        });
        
        // 生成统计概览
        builder.AppendLine("## 统计概览");
        builder.AppendLine();
        builder.AppendLine($"- 总问题数：{allIssues.Count}");
        builder.AppendLine($"- 按状态分布：");
        foreach (var group in allIssues.GroupBy(i => i.Status).OrderBy(g => g.Key)) {
            builder.AppendLine($"  - {group.Key}：{group.Count()}个");
        }
        builder.AppendLine();
        
        // 按状态分组输出
        foreach (var statusGroup in allIssues.GroupBy(i => i.Status).OrderBy(g => g.Key)) {
            builder.AppendLine($"## {statusGroup.Key}的问题");
            builder.AppendLine();
            
            builder.AppendLine("| 问题描述 | 来源文档 | 负责人 |");
            builder.AppendLine("|:---------|:---------|:-------|");
            
            foreach (var issue in statusGroup.OrderBy(i => i.SourceDocument)) {
                builder.AppendLine($"| {issue.Description} | [{Path.GetFileName(issue.SourceDocument)}]({issue.SourceDocument}) | {issue.Assignee ?? "未分配"} |");
            }
            
            builder.AppendLine();
        }
        
        return builder.ToString();
    }
    
    private List<Issue> ExtractIssues(DocumentNode node, IEnumerable<object> issues) {
        // 提取问题信息的具体实现
        var result = new List<Issue>();
        // ... 实现细节
        return result;
    }
    
    private class Issue {
        public string Description { get; set; } = "";
        public string Status { get; set; } = "open";
        public string? Assignee { get; set; }
        public string SourceDocument { get; set; } = "";
    }
}
```

---

## 5. 错误码定义

### 5.1 错误码命名规范
所有错误码使用 `DOCGRAPH_` 前缀，格式：`DOCGRAPH_{CATEGORY}_{DESCRIPTION}`

### 5.2 核心错误码

| 错误码 | 严重度 | 说明 |
|:-------|:-------|:-----|
| `DOCGRAPH_FRONTMATTER_REQUIRED_FIELD_MISSING` | Error | 必填字段缺失 |
| `DOCGRAPH_FRONTMATTER_FIELD_TYPE_MISMATCH` | Error | 字段类型不匹配 |
| `DOCGRAPH_FRONTMATTER_FIELD_VALUE_INVALID` | Warning | 字段值无效 |
| `DOCGRAPH_RELATION_DANGLING_LINK` | Error | 悬空引用（目标文档不存在） |
| `DOCGRAPH_RELATION_MISSING_BACKLINK` | Warning | 缺失反向链接 |
| `DOCGRAPH_YAML_PARSE_ERROR` | Error | YAML解析失败 |
| `DOCGRAPH_YAML_ALIAS_DETECTED` | Error | 检测到YAML anchor/alias（禁止） |
| `DOCGRAPH_IO_DECODE_FAILED` | Error | 文件编码解码失败 |
| `DOCGRAPH_PATH_OUT_OF_WORKSPACE` | Error | 路径越界（超出workspace范围） |
| `DOCGRAPH_FIX_CREATE_FAILED` | Error | 文件创建失败 |
| `DOCGRAPH_FIX_TARGET_EXISTS` | Warning | 目标文件已存在（跳过创建） |
| `DOCGRAPH_FIX_VALIDATION_BLOCKED` | Error | 验证错误阻止修复执行 |
| `DOCGRAPH_FIX_USER_CANCELLED` | Info | 用户取消修复操作 |
| `DOCGRAPH_FIX_DRYRUN_ONLY` | Info | dry-run模式，未实际执行 |

---

## 6. 退出码约定

### 6.1 基础退出码

| 退出码 | 含义 | 使用场景 |
|:-------|:-----|:---------|
| 0 | 成功 | 无错误，无警告 |
| 1 | 警告 | 有警告，无错误 |
| 2 | 错误 | 有验证错误 |
| 3 | 致命 | 无法执行（配置错误、IO错误） |

### 6.2 修复模式退出码（`--fix` 模式）

| 退出码 | 场景 | 说明 |
|:-------|:-----|:-----|
| 0 | 验证通过 + 修复全部成功（或无需修复） | 修复执行成功或无修复需求 |
| 1 | 验证有警告 + 修复成功 | 警告不影响修复执行 |
| 2 | 验证有错误，未执行修复 | 错误阻止修复执行 |
| 3 | 验证 Fatal 或修复执行失败 | Fatal错误或修复执行中失败 |

**注意**：修复模式退出码优先于基础退出码。当指定 `--fix` 时，使用修复模式退出码语义。

---

## 7. 演进考虑

### 7.1 v1.0 扩展点预留
1. **节点粒度Visitor**：为复杂聚合逻辑预留
2. **配置驱动**：基于经验的配置系统
3. **插件架构**：动态加载Visitor

### 7.2 数据兼容性
- v0.1的输出可作为v1.0的输入
- v0.1的错误码体系可扩展
- v0.1的Visitor接口保持兼容

### 7.3 性能优化路径
1. **增量扫描**：只处理变更的文件
2. **缓存机制**：缓存解析结果
3. **并行处理**：多线程处理大量文件

---

## 8. 使用示例

### 8.1 基本使用
```csharp
// 构建文档图
var builder = new DocumentGraphBuilder();
var graph = builder.Build();

// 基础验证
var validationResult = builder.Validate(graph);
if (!validationResult.IsValid) {
    // 输出验证报告
    Console.WriteLine(validationResult.ToMarkdown());
}

// 验证并修复（批量预览模式）
var fixOptions = new FixOptions { Enabled = true };
var fixResult = builder.Validate(graph, fixOptions);

// 只预览不执行（dry-run）
var dryRunOptions = new FixOptions { Enabled = true, DryRun = true };
var dryRunResult = builder.Validate(graph, dryRunOptions);

// 自动执行（CI/CD场景）
var autoFixOptions = new FixOptions { Enabled = true, AutoConfirm = true };
var autoFixResult = builder.Validate(graph, autoFixOptions);

// 生成汇总文档
var visitors = new List<IDocumentGraphVisitor> {
    new GlossaryVisitor(),
    new IssueAggregator()
};

foreach (var visitor in visitors) {
    try {
        var output = visitor.Generate(graph);
        File.WriteAllText(visitor.OutputPath, output);
    }
    catch (Exception ex) {
        // Visitor执行失败，记录错误但继续执行其他
        Console.WriteLine($"Visitor {visitor.Name} 执行失败：{ex.Message}");
    }
}
```

### 8.2 命令行使用
```bash
# 基础验证
docgraph validate

# 验证并修复（批量预览模式）
docgraph validate --fix

# 只预览不执行（dry-run）
docgraph validate --fix --dry-run

# 自动执行（CI/CD场景）
docgraph validate --fix --yes

# 生成所有汇总文档
docgraph generate

# 生成特定汇总文档
docgraph generate glossary
docgraph generate issues

# 详细输出
docgraph validate --fix --verbose

# 输出JSON格式报告（机器可读）
docgraph validate --fix --output json
```

---

**变更记录**：
- v0.1.0-draft (2026-01-01)：基于畅谈会共识创建初始草案

**下一步**：基于此API设计创建spec.md实现规范。