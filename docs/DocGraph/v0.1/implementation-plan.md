# DocGraph v0.1 - 实施计划

> **版本**：v0.1.0  
> **创建日期**：2026-01-01  
> **状态**：准备开始  
> **目的**：基于畅谈会共识，制定具体的编码实施计划

---

## 1. 实施基础

### 1.1 设计文档状态
- ✅ **scope.md**：功能边界文档（已修正路径处理矛盾）
- ✅ **api.md**：API设计文档（已更新修复模式接口）
- ✅ **spec.md**：实现规范文档（已添加修复相关条款）
- ✅ **todo.md**：问题追踪（问题1已解决，问题5部分解决）
- ✅ **畅谈会记录**：完整的讨论和决策过程

### 1.2 技术栈
- **语言**：C# (.NET 9.0+)
- **依赖**：YamlDotNet (≥13.0.0), System.CommandLine (≥2.0.0), xUnit (≥2.5.0)
- **工具**：VS Code / Visual Studio, Git, xUnit测试框架

### 1.3 项目结构规划
```
atelia/src/DocGraph/
├── DocGraph.csproj
├── Program.cs                    # CLI入口
├── Commands/
│   └── ValidateCommand.cs       # validate命令实现
├── Core/
│   ├── DocumentGraphBuilder.cs  # 文档图构建器
│   ├── DocumentGraph.cs         # 文档图数据模型
│   ├── ValidationResult.cs      # 验证结果
│   └── Fix/                     # 修复功能
│       ├── FixOptions.cs
│       ├── FixResult.cs
│       ├── IFixAction.cs
│       └── CreateMissingFileAction.cs
├── Visitors/
│   ├── IDocumentGraphVisitor.cs
│   ├── GlossaryVisitor.cs
│   └── IssueAggregator.cs
└── Utils/
    ├── PathNormalizer.cs
    ├── FrontmatterParser.cs
    └── TemplateGenerator.cs
```

---

## 2. 实施阶段规划

### 2.1 第一阶段：核心框架（Day 1-2）

#### Day 1 任务清单
```
✅ 创建项目结构和基本配置
✅ 实现基础数据模型（DocumentNode, DocumentGraph）
✅ 实现PathNormalizer基本功能
✅ 实现FrontmatterParser基础解析
✅ 创建DocumentGraphBuilder骨架
```

#### Day 2 任务清单
```
□ 完善DocumentGraphBuilder.Build()方法
□ 实现wish目录扫描和文件过滤
□ 实现produce关系提取和闭包构建
□ 实现基础验证逻辑（必填字段、路径存在性）
□ 创建ValidationResult和错误报告格式
```

### 2.2 第二阶段：修复功能（Day 3）

#### Day 3 任务清单
```
□ 实现ValidateCommand基础结构
□ 添加--fix参数解析和处理
□ 实现FixOptions和修复上下文
□ 实现CreateMissingFileAction（v0.1唯一修复类型）
□ 添加--dry-run参数实现
□ 添加--yes参数实现
□ 实现批量预览输出格式
□ 实现简单确认提示（Y/n）
```

### 2.3 第三阶段：Visitor和测试（Day 4）

#### Day 4 任务清单
```
□ 实现IDocumentGraphVisitor接口
□ 实现GlossaryVisitor（术语表生成器）
□ 实现IssueAggregator（问题汇总器）
□ 编写核心功能单元测试
□ 创建测试数据目录结构
□ 实现基础CLI集成测试
```

### 2.4 第四阶段：完善和文档（Day 5）

#### Day 5 任务清单
```
□ 完善错误处理（三层建议结构）
□ 添加详细日志和进度反馈
□ 实现大规模操作安全升级逻辑（>5文件）
□ 编写用户文档和示例
□ 性能优化和边界测试
□ 创建发布包和安装说明
```

---

## 3. 详细任务分解

### 3.1 核心数据模型实现

#### 任务：DocumentNode类
```csharp
// 需要实现的属性
public class DocumentNode {
    public string FilePath { get; }           // workspace相对路径
    public string DocId { get; }              // 文档标识
    public string Title { get; }              // 文档标题
    public string? Status { get; }            // 状态（Wish文档）
    public IReadOnlyDictionary<string, object> Frontmatter { get; }
    public IReadOnlyList<DocumentNode> Produces { get; }
    public IReadOnlyList<DocumentNode> ProducedBy { get; }
}
```

#### 任务：DocumentGraph类
```csharp
public class DocumentGraph {
    public IReadOnlyList<DocumentNode> RootNodes { get; }
    public IReadOnlyList<DocumentNode> AllNodes { get; }
    public IReadOnlyDictionary<string, DocumentNode> ByPath { get; }
    public void ForEachDocument(Action<DocumentNode> visitor) { ... }
}
```

### 3.2 路径规范化实现

#### 任务：PathNormalizer类
```csharp
public static class PathNormalizer {
    // 基本规范化：消解./..，统一分隔符
    public static string Normalize(string path) { ... }
    
    // 检查路径是否在workspace内
    public static bool IsWithinWorkspace(string path, string workspaceRoot) { ... }
    
    // 转换为workspace相对路径
    public static string ToWorkspaceRelative(string absolutePath, string workspaceRoot) { ... }
}
```

### 3.3 修复功能实现

#### 任务：IFixAction接口
```csharp
public interface IFixAction {
    bool CanExecute(FixContext context);
    string Describe();
    string Preview();  // dry-run使用
    FixResult Execute(IFileSystem fs);
}

// v0.1唯一实现
public class CreateMissingFileAction : IFixAction {
    private readonly string targetPath;
    private readonly string sourceDocId;
    
    public bool CanExecute(FixContext context) {
        return !File.Exists(targetPath) && 
               context.Graph.ByPath.ContainsKey(sourceDocId);
    }
    
    public FixResult Execute(IFileSystem fs) {
        var template = GenerateTemplate(targetPath, sourceDocId);
        fs.WriteAllText(targetPath, template);
        return FixResult.Success(targetPath, FixActionType.CreateFile);
    }
}
```

### 3.4 CLI命令实现

#### 任务：ValidateCommand类
```csharp
public class ValidateCommand : Command {
    public ValidateCommand() : base("validate", "验证文档关系") {
        // 参数定义
        var pathArgument = new Argument<string>("path", () => ".", "要验证的目录路径");
        var fixOption = new Option<bool>("--fix", "修复可自动修复的问题");
        var dryRunOption = new Option<bool>("--dry-run", "只显示会执行的操作，不实际执行");
        var yesOption = new Option<bool>(new[] { "--yes", "-y" }, "跳过确认提示，自动执行");
        
        this.AddArgument(pathArgument);
        this.AddOption(fixOption);
        this.AddOption(dryRunOption);
        this.AddOption(yesOption);
        
        this.SetHandler(ExecuteAsync, pathArgument, fixOption, dryRunOption, yesOption);
    }
    
    private async Task ExecuteAsync(string path, bool fix, bool dryRun, bool yes) {
        // 实现验证和修复逻辑
    }
}
```

---

## 4. 测试策略

### 4.1 单元测试覆盖

#### 核心功能测试
```
□ PathNormalizer测试：基本规范化、边界情况、越界检查
□ FrontmatterParser测试：YAML解析、字段提取、错误处理
□ DocumentGraphBuilder测试：扫描、关系提取、闭包构建
□ FixAction测试：创建文件、条件检查、模板生成
□ Visitor测试：术语表生成、问题汇总
```

#### 集成测试
```
□ CLI命令测试：参数解析、交互流程、退出码
□ 端到端测试：完整工作流验证
□ 性能测试：大量文件处理性能
```

### 4.2 测试数据目录

```
tests/data/
├── valid/                    # 有效测试数据
│   ├── wishes/
│   │   ├── active/
│   │   │   └── wish-0001.md
│   │   └── completed/
│   │       └── wish-0002.md
│   └── artifacts/
│       ├── api.md
│       └── spec.md
├── invalid/                  # 无效测试数据
│   ├── missing-field.md
│   ├── invalid-path.md
│   └── circular-reference/
└── expected/                # 预期输出
    ├── glossary.gen.md.snapshot
    ├── issues.gen.md.snapshot
    └── validation-report.json.snapshot
```

---

## 5. 质量保证

### 5.1 代码质量要求
- **测试覆盖率**：核心逻辑 ≥ 80%
- **代码规范**：遵循C#编码规范，使用.NET Analyzers
- **错误处理**：所有可能失败的操作都有适当的错误处理
- **日志记录**：关键操作有适当的日志记录

### 5.2 验收标准
基于spec.md的验收标准矩阵，必须通过：
- **F-001**：扫描wish目录测试
- **F-002**：推导字段计算测试  
- **F-003**：produce关系提取测试
- **F-004**：文档图闭包构建测试
- **F-005**：双向链接验证测试
- **F-006**：术语表生成测试
- **F-007**：问题汇总生成测试

### 5.3 性能要求
- **响应时间**：1000文档处理 < 2秒
- **内存使用**：1000文档处理 < 200MB
- **文件操作**：原子写入，失败不污染

---

## 6. 风险管理

### 6.1 技术风险
| 风险 | 可能性 | 影响 | 缓解策略 |
|:-----|:-------|:-----|:---------|
| 路径处理边界情况 | 中 | 中 | v0.1简化处理，记录警告 |
| YAML解析性能 | 低 | 中 | 实施资源上限约束 |
| 并发问题 | 低 | 低 | v0.1单线程，避免并发 |

### 6.2 进度风险
| 风险 | 可能性 | 影响 | 缓解策略 |
|:-----|:-------|:-----|:---------|
| 修复功能复杂度 | 中 | 中 | 聚焦v0.1简化实现 |
| 测试覆盖不足 | 高 | 中 | 优先核心功能测试 |
| 文档编写耗时 | 中 | 低 | 代码和文档并行 |

### 6.3 质量风险
| 风险 | 可能性 | 影响 | 缓解策略 |
|:-----|:-------|:-----|:---------|
| 与规范不一致 | 中 | 高 | 严格遵循spec.md条款 |
| 用户体验问题 | 中 | 中 | 遵循Curator的UX设计 |
| 跨平台问题 | 低 | 中 | 基础路径处理，记录问题 |

---

## 7. 沟通和协作

### 7.1 每日进度同步
- **晨会**：每日开始前同步进度和计划
- **问题跟踪**：使用todo.md记录遇到的问题
- **代码审查**：关键功能完成后进行代码审查

### 7.2 文档更新
- **设计文档**：实施过程中发现的问题及时更新设计文档
- **用户文档**：随着功能实现逐步完善用户文档
- **变更记录**：记录所有重要的设计变更

### 7.3 质量检查点
- **Day 2结束**：核心框架完成，基本验证功能可用
- **Day 3结束**：修复功能完成，CLI基本可用
- **Day 4结束**：Visitor功能完成，测试覆盖达标
- **Day 5结束**：完整功能完成，准备发布

---

## 8. 成功标准

### 8.1 功能成功标准
- ✅ 能扫描wish目录并构建文档图
- ✅ 能验证produce关系完整性
- ✅ 能创建缺失的产物文件（--fix模式）
- ✅ 能生成术语表和问题汇总（Visitor）
- ✅ CLI接口完整可用

### 8.2 质量成功标准
- ✅ 通过所有验收测试
- ✅ 核心逻辑测试覆盖率 ≥ 80%
- ✅ 错误处理完整，退出码正确
- ✅ 性能满足要求（1000文档 < 2秒）

### 8.3 用户体验成功标准
- ✅ CLI帮助信息完整清晰
- ✅ 错误信息有帮助，能指导修复
- ✅ 批量预览和确认流程顺畅
- ✅ dry-run模式能预览操作

---

## 9. 开始实施

### 立即开始的任务
1. **创建项目结构**：按照3.1节规划创建目录和文件
2. **设置开发环境**：配置.NET 9.0+，安装依赖包
3. **实现基础数据模型**：DocumentNode和DocumentGraph类
4. **编写第一个测试**：PathNormalizer基础功能测试

### 第一天目标
- 项目结构就绪
- 基础数据模型实现
- 路径规范化基本功能
- 第一个通过的单元测试

---

**变更记录**：
- v0.1.0 (2026-01-01)：基于畅谈会共识创建初始实施计划

**下一步**：立即开始Day 1任务实施！