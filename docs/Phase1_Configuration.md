# MemoTree 配置选项和系统设置 (Phase 1)

> 版本: v1.0
> 创建日期: 2025-07-25
> 基于: Core_Types_Design.md 第6节
> 依赖: Phase1_CoreTypes.md

## 概述

本文档定义了MemoTree系统的配置选项和系统设置类型。这些配置类型为系统的各个组件提供了灵活的配置能力，支持工作空间结构、存储策略、关系管理和检索功能的定制化配置。

作为第一阶段的基础设施，这些配置类型必须在系统启动前完成定义，为后续的存储层、服务层提供配置支持。

## 1. 系统配置

### 1.1 主系统配置

```csharp
/// <summary>
/// MemoTree系统配置选项
/// 对应MVP设计草稿中定义的Workspace结构
/// </summary>
public class MemoTreeOptions
{
    /// <summary>
    /// 工作空间根目录路径
    /// </summary>
    public string WorkspaceRoot { get; set; } = "./workspace";

    /// <summary>
    /// 认知节点存储目录名 (对应MVP设计中的CogNodes/)
    /// </summary>
    public string CogNodesDirectory { get; set; } = "CogNodes";

    /// <summary>
    /// 父子关系存储目录名 (对应MVP设计中的ParentChildrens/)
    /// </summary>
    public string ParentChildrensDirectory { get; set; } = "ParentChildrens";

    /// <summary>
    /// 语义关系数据存储目录名 (对应MVP设计中的Relations/)
    /// </summary>
    public string RelationsDirectory { get; set; } = "Relations";

    /// <summary>
    /// 单个认知节点的默认最大上下文Token数
    /// 用于限制单个CogNode内容的Token数量，不能超过SystemLimits.DefaultMaxContextTokens
    /// </summary>
    public int DefaultMaxContextTokens { get; set; } = 8000;

    /// <summary>
    /// 整个MemoTree视图的最大Token数
    /// 用于限制整个视图展开后的总Token数量，范围应在128K-200K之间
    /// </summary>
    public int MaxMemoTreeViewTokens { get; set; } = 150_000;

    /// <summary>
    /// 自动保存间隔（分钟）
    /// </summary>
    public int AutoSaveIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// 是否启用Git版本控制
    /// </summary>
    public bool EnableVersionControl { get; set; } = true;

    /// <summary>
    /// 是否启用Roslyn集成 (Phase 4功能，当前阶段默认关闭)
    /// </summary>
    public bool EnableRoslynIntegration { get; set; } = false;

    /// <summary>
    /// MVP模式：使用Fast Fail异常处理策略
    /// true: 所有异常直接向上传播，保持故障现场完整性
    /// false: 使用完整的异常处理和恢复机制 (Phase 5功能)
    /// </summary>
    public bool UseMvpFastFailMode { get; set; } = true;

    /// <summary>
    /// 支持的文件扩展名
    /// </summary>
    public IList<string> SupportedFileExtensions { get; set; } = new List<string>
    {
        ".md", ".txt", ".cs", ".json", ".yaml", ".yml"
    };
}
```

## 2. 存储配置

### 2.1 存储选项配置

```csharp
/// <summary>
/// 存储配置选项
/// </summary>
public class StorageOptions
{
    /// <summary>
    /// 元数据文件名
    /// </summary>
    public string MetadataFileName { get; set; } = "meta.yaml";

    /// <summary>
    /// 详细内容文件名 (对应MVP设计中的detail.md)
    /// </summary>
    public string DetailContentFileName { get; set; } = "detail.md";

    /// <summary>
    /// 摘要内容文件名 (对应MVP设计中的summary.md)
    /// </summary>
    public string SummaryContentFileName { get; set; } = "summary.md";

    /// <summary>
    /// 简介级内容文件名 (对应MVP设计中的brief.md和LodLevel.Brief)
    /// </summary>
    public string BriefContentFileName { get; set; } = "brief.md";

    /// <summary>
    /// 外部链接文件名
    /// </summary>
    public string ExternalLinksFileName { get; set; } = "external-links.json";

    /// <summary>
    /// 父子关系文件扩展名
    /// </summary>
    public string ParentChildrensFileExtension { get; set; } = ".yaml";

    /// <summary>
    /// 语义关系数据文件名
    /// </summary>
    public string RelationsFileName { get; set; } = "relations.yaml";

    /// <summary>
    /// 关系类型定义文件名
    /// </summary>
    public string RelationTypesFileName { get; set; } = "relation-types.yaml";

    /// <summary>
    /// 内容哈希算法
    /// </summary>
    public string HashAlgorithm { get; set; } = "SHA256";
}
```

## 3. 关系管理配置

### 3.1 关系选项配置

```csharp
/// <summary>
/// 关系管理配置选项
/// 专注于关系处理的行为逻辑，不包含路径信息
/// 路径信息由MemoTreeOptions统一管理
/// </summary>
public class RelationOptions
{
    /// <summary>
    /// 是否启用父子关系独立存储
    /// </summary>
    public bool EnableIndependentHierarchyStorage { get; set; } = true;

    /// <summary>
    /// 是否启用语义关系数据集中存储
    /// </summary>
    public bool EnableCentralizedRelationStorage { get; set; } = true;

    /// <summary>
    /// 最大关系深度
    /// </summary>
    public int MaxRelationDepth { get; set; } = 10;

    /// <summary>
    /// 关系图最大节点数
    /// </summary>
    public int MaxRelationGraphNodes { get; set; } = 1000;

    /// <summary>
    /// 是否启用关系验证
    /// </summary>
    public bool EnableRelationValidation { get; set; } = true;

    /// <summary>
    /// 是否自动清理孤立的语义关系
    /// </summary>
    public bool AutoCleanupOrphanedRelations { get; set; } = true;

    /// <summary>
    /// 最大内存中关系数量
    /// </summary>
    public int MaxInMemoryRelations { get; set; } = 10000;

    /// <summary>
    /// 是否启用关系索引优化
    /// </summary>
    public bool EnableRelationIndexing { get; set; } = true;

    /// <summary>
    /// 关系数据批量写入大小
    /// </summary>
    public int RelationBatchWriteSize { get; set; } = 100;
}
```

## 4. 检索配置

### 4.1 检索选项配置

```csharp
/// <summary>
/// 检索配置选项
/// </summary>
public class RetrievalOptions
{
    /// <summary>
    /// 是否启用全文搜索 (基于Lucene.Net)
    /// </summary>
    public bool EnableFullTextSearch { get; set; } = true;

    /// <summary>
    /// 是否启用语义搜索 (基于向量检索)
    /// </summary>
    public bool EnableSemanticSearch { get; set; } = false;

    /// <summary>
    /// 全文搜索索引目录
    /// </summary>
    public string FullTextIndexDirectory { get; set; } = "./indexes/fulltext";

    /// <summary>
    /// 语义搜索向量维度
    /// </summary>
    public int VectorDimension { get; set; } = 768;

    /// <summary>
    /// 语义搜索索引目录
    /// </summary>
    public string SemanticIndexDirectory { get; set; } = "./indexes/semantic";

    /// <summary>
    /// 搜索结果最大数量
    /// </summary>
    public int MaxSearchResults { get; set; } = 50;
}
```

## 5. 视图状态配置

### 视图状态配置选项

```csharp
/// <summary>
/// 视图状态配置选项 - 内存优先架构
/// 对应Phase2_ViewStorage.md中定义的视图状态存储和内存管理
/// </summary>
public class ViewOptions
{
    /// <summary>
    /// 视图状态文件名
    /// </summary>
    public string ViewStateFileName { get; set; } = "last-view.json";

    /// <summary>
    /// 视图状态备份文件名
    /// </summary>
    public string ViewStateBackupFileName { get; set; } = "view-state-backup.json";

    /// <summary>
    /// 最大内存中视图状态数量
    /// </summary>
    public int MaxInMemoryViewStates { get; set; } = 1000;

    /// <summary>
    /// 是否启用视图状态自动保存
    /// </summary>
    public bool EnableAutoSaveViewState { get; set; } = true;

    /// <summary>
    /// 视图状态自动保存间隔（秒）
    /// </summary>
    public int ViewStateAutoSaveIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 是否启用批量视图状态更新
    /// </summary>
    public bool EnableBatchViewStateUpdates { get; set; } = true;

    /// <summary>
    /// 批量更新间隔（毫秒）
    /// </summary>
    public int BatchUpdateIntervalMilliseconds { get; set; } = 100;

    /// <summary>
    /// 是否启用视图状态压缩存储
    /// </summary>
    public bool EnableViewStateCompression { get; set; } = false;

    /// <summary>
    /// 视图状态批量操作的最大数量
    /// </summary>
    public int MaxBatchViewStateOperations { get; set; } = 20;

    /// <summary>
    /// 是否启用视图状态预加载
    /// </summary>
    public bool EnableViewStatePreloading { get; set; } = true;
}
```

## 6. 配置验证和最佳实践

> MVP 验证策略：采用“启动时配置校验 + Fast Fail”的方式尽早暴露问题；运行时热重载与复杂校验机制在 Phase 3+ 实施。


### 6.1 配置验证规则

配置类型应遵循以下验证规则：

1. **路径配置验证**
   - 所有目录路径必须是有效的文件系统路径
   - 相对路径将基于工作空间根目录解析
   - 确保配置的目录具有适当的读写权限

2. **数值配置验证**
   - Token数量、时间间隔等数值配置必须为正数
   - 单个节点Token数不能超过SystemLimits.DefaultMaxContextTokens (8000)
   - MemoTree视图Token数必须在128K-200K范围内
   - 关系图节点数不能超过NodeConstraints.MaxChildrenCount (1000)
   - 关系深度不能超过NodeConstraints.MaxTreeDepth (20)
   - 若使用允许的轻量结果/索引缓存（仅限外部系统/昂贵查询），其过期时间不应小于1分钟；不引入独立二级缓存
   - 最大节点数等限制应根据系统资源合理设置

3. **功能开关验证**
   - 相关功能的依赖关系检查
   - 例如：启用语义搜索时必须配置向量维度

### 6.2 配置最佳实践

1. **开发环境配置**
   ```csharp
   var devOptions = new MemoTreeOptions
   {
       WorkspaceRoot = "./dev-workspace",
       AutoSaveIntervalMinutes = 1,
       EnableVersionControl = false,
       DefaultMaxContextTokens = 4000,        // 单个节点Token限制
       MaxMemoTreeViewTokens = 128_000        // 整个视图Token限制
   };
   ```

2. **生产环境配置**
   ```csharp
   var prodOptions = new MemoTreeOptions
   {
       WorkspaceRoot = "/var/memotree/workspace",
       AutoSaveIntervalMinutes = 5,
       EnableVersionControl = true,
       DefaultMaxContextTokens = 8000,        // 单个节点Token限制
       MaxMemoTreeViewTokens = 180_000        // 整个视图Token限制
   };
   ```

3. **性能优化配置**
   ```csharp
   var perfOptions = new RelationOptions
   {
       MaxInMemoryRelations = 10000,
       EnableRelationIndexing = true,
       MaxRelationGraphNodes = 2000,
       AutoCleanupOrphanedRelations = true
   };
   ```

## 7. 配置扩展性

### 7.1 自定义配置支持

系统支持通过以下方式扩展配置：

1. **配置文件支持**
   - 支持JSON、YAML格式的配置文件
   - 支持环境变量覆盖
   - 支持配置热重载

2. **配置验证接口**
   ```csharp
   public interface IConfigurationValidator<T>
   {
       ValidationResult Validate(T configuration);
   }

   // 专用的MemoTree配置验证器
   public interface IMemoTreeConfigurationValidator
   {
       ValidationResult ValidateMemoTreeOptions(MemoTreeOptions options);
       ValidationResult ValidateRelationOptions(RelationOptions options);
       ValidationResult ValidateTokenLimits(int nodeTokens, int viewTokens);
   }
   ```

3. **配置变更通知**
   - 配置变更时触发相应事件
   - 支持组件级别的配置更新响应

## 实施优先级

### 高优先级 (Phase 1)
- [x] **MemoTreeOptions**: 系统核心配置，必须首先实现
- [x] **StorageOptions**: 存储层配置，为Phase 2存储实现提供基础
- [x] **RelationOptions**: 关系管理配置，支持MVP中的关系存储策略

### 中优先级 (Phase 2)
- [ ] **RetrievalOptions**: 检索功能配置，配合检索服务实现
- [ ] **配置验证机制**: 确保配置的正确性和一致性

### 低优先级 (Phase 3+)
- [ ] **配置热重载**: 运行时配置更新能力
- [ ] **高级配置扩展**: 插件化配置支持

## 最佳实践指南

1. **配置管理**
   - 使用强类型配置类，避免魔法字符串
   - 为所有配置项提供合理的默认值
   - 在系统启动时验证配置的完整性

2. **性能考虑**
   - 缓存配置对象，避免重复解析
   - 对于频繁访问的配置项，考虑使用静态缓存
   - 合理设置缓存过期时间，平衡性能和内存使用

3. **安全性**
   - 敏感配置项（如密钥）应使用安全存储
   - 配置文件权限应适当限制
   - 避免在日志中输出敏感配置信息

4. **可维护性**
   - 配置项应有清晰的命名和文档
   - 相关配置项应组织在同一配置类中
   - 避免配置项之间的隐式依赖关系

5. **测试友好**
   - 配置类应支持依赖注入
   - 提供测试专用的配置预设
   - 支持配置的模拟和覆盖

## 8. 配置集成示例

### 8.1 依赖注入配置

```csharp
// Program.cs 或 Startup.cs
services.Configure<MemoTreeOptions>(configuration.GetSection("MemoTree"));
services.Configure<StorageOptions>(configuration.GetSection("Storage"));
services.Configure<RelationOptions>(configuration.GetSection("Relations"));
services.Configure<RetrievalOptions>(configuration.GetSection("Retrieval"));
services.Configure<ViewOptions>(configuration.GetSection("View"));
```

### 8.2 配置使用示例

```csharp
public class NodeStorageService
{
    private readonly MemoTreeOptions _memoTreeOptions;
    private readonly StorageOptions _storageOptions;

    public NodeStorageService(
        IOptions<MemoTreeOptions> memoTreeOptions,
        IOptions<StorageOptions> storageOptions)
    {
        _memoTreeOptions = memoTreeOptions.Value;
        _storageOptions = storageOptions.Value;
    }

    public string GetNodeMetadataPath(NodeId nodeId)
    {
        return Path.Combine(
            _memoTreeOptions.WorkspaceRoot,
            _memoTreeOptions.CogNodesDirectory,
            nodeId.Value,
            _storageOptions.MetadataFileName);
    }
}

/// <summary>
/// 关系存储服务示例 - 展示多配置注入的正确模式
/// </summary>
public class RelationStorageService
{
    private readonly MemoTreeOptions _memoTreeOptions;
    private readonly RelationOptions _relationOptions;

    public RelationStorageService(
        IOptions<MemoTreeOptions> memoTreeOptions,
        IOptions<RelationOptions> relationOptions)
    {
        _memoTreeOptions = memoTreeOptions.Value;
        _relationOptions = relationOptions.Value;
    }

    /// <summary>
    /// 获取父子关系存储路径
    /// 路径信息来自MemoTreeOptions，行为配置来自RelationOptions
    /// </summary>
    public string GetHierarchyStoragePath()
    {
        if (!_relationOptions.EnableIndependentHierarchyStorage)
            throw new InvalidOperationException("父子关系独立存储未启用");

        return Path.Combine(
            _memoTreeOptions.WorkspaceRoot,
            _memoTreeOptions.ParentChildrensDirectory);
    }

    /// <summary>
    /// 获取语义关系存储路径
    /// 路径信息来自MemoTreeOptions，行为配置来自RelationOptions
    /// </summary>
    public string GetRelationStoragePath()
    {
        if (!_relationOptions.EnableCentralizedRelationStorage)
            throw new InvalidOperationException("语义关系集中存储未启用");

        return Path.Combine(
            _memoTreeOptions.WorkspaceRoot,
            _memoTreeOptions.RelationsDirectory);
    }
}
```

### 8.3 配置文件示例

```yaml
# appsettings.yml
MemoTree:
  WorkspaceRoot: "./workspace"
  CogNodesDirectory: "CogNodes"
  DefaultMaxContextTokens: 8000          # 单个节点Token限制
  MaxMemoTreeViewTokens: 150000          # 整个视图Token限制
  AutoSaveIntervalMinutes: 5
  EnableVersionControl: true

Storage:
  MetadataFileName: "meta.yaml"
  DetailContentFileName: "detail.md"
  HashAlgorithm: "SHA256"

Relations:
  EnableIndependentHierarchyStorage: true
  MaxRelationDepth: 10
  MaxInMemoryRelations: 10000
  EnableRelationIndexing: true

Retrieval:
  EnableFullTextSearch: true
  EnableSemanticSearch: false
  MaxSearchResults: 50

View:
  ViewStateFileName: "last-view.json"
  ViewStateBackupFileName: "view-state-backup.json"
  MaxInMemoryViewStates: 1000
  EnableAutoSaveViewState: true
  ViewStateAutoSaveIntervalSeconds: 30
  EnableBatchViewStateUpdates: true
```

---

**下一阶段**: [Phase2_StorageInterfaces.md](Phase2_StorageInterfaces.md) - 存储接口定义
