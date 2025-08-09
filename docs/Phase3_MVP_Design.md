# MemoTree Phase3 MVP 主干设计

> **创建日期**: 2025-08-09  
> **会话背景**: 与刘世超讨论MemoTree实施策略  
> **目标**: 实现可工作的MemoTree原型，支持基础树形操作和CLI工具  
> **状态**: 设计阶段

## 🎯 MVP策略与决策依据

### 核心理念
**渐进式实现**: 先实现核心主干功能，快速验证架构可行性，再逐步添加高级特性。

### 关键决策

#### 1. 简化LOD机制 (2025-08-09决策)
**决策**: 展开=Full级别，折叠=Gist级别，忽略中间的Summary级别
**依据**: 
- MVP阶段避免复杂度，专注核心交互体验
- 类似GUI TreeView的直观行为：展开显示全部内容，折叠只显示标题
- 用户心智模型简单：二元状态比多级状态更容易理解

#### 2. 字符数替代Token估算 (2025-08-09决策)
**决策**: 暂时使用字符数统计，不实现Token精确计算
**依据**:
- Token估算是技术大坑：不同LLM使用不同tokenizer
- 实时请求tokenizer API延迟高，影响用户体验
- 缓存Token结果需要处理一致性问题，增加复杂度
- 字符数作为粗略估算在MVP阶段足够使用

#### 3. Git风格工作空间管理 (2025-08-09决策)
**决策**: 采用`.memotree/`目录，类似`.git/`的工作模式
**依据**:
- 用户对Git工作流已有认知基础，学习成本低
- 支持为任何项目附加MemoTree功能
- 避免全局路径管理的复杂性，用cd切换工作空间

#### 4. 软链接工作空间 (connect命令)
**决策**: 支持`memotree connect <workspace-path>`创建软链接工作空间
**依据**:
- 解决跨项目认知连续性问题
- 支持多个项目共享同一个认知仓库
- 类似符号链接的概念，技术实现相对简单

## 🏗️ 技术架构设计

### 简化的数据模型

#### SimpleCognitiveNode (MVP版本)
```csharp
public record SimpleCognitiveNode
{
    public NodeId Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;  // 单一内容，无多级LOD
    public NodeType Type { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    // 暂不支持：多级LOD内容、语义关系、自定义属性
}
```

#### ViewState (简化版)
```csharp
public record SimpleViewState
{
    public Dictionary<NodeId, LodLevel> NodeStates { get; init; } = new();
    public NodeId? FocusNodeId { get; init; }
    public int TotalCharacters { get; init; }  // 使用字符数而非Token数
    public DateTime LastAccessTime { get; init; }
}
```

### 服务接口简化

#### IMemoTreeService (MVP版本)
```csharp
public interface IMemoTreeService
{
    // 核心渲染功能
    Task<string> RenderViewAsync(string viewName = "default", CancellationToken cancellationToken = default);
    
    // 简化的展开/折叠 (忽略LodLevel参数，展开=Full，折叠=Gist)
    Task ExpandNodeAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default);
    Task CollapseNodeAsync(NodeId nodeId, string viewName = "default", CancellationToken cancellationToken = default);
    
    // 树结构查询
    Task<IReadOnlyList<NodeTreeItem>> GetNodeTreeAsync(NodeId? rootId = null, CancellationToken cancellationToken = default);
    
    // 统计信息
    Task<ViewStats> GetViewStatsAsync(string viewName = "default", CancellationToken cancellationToken = default);
}
```

#### IMemoTreeEditor (MVP版本)
```csharp
public interface IMemoTreeEditor
{
    // 基础CRUD操作
    Task<NodeId> CreateNodeAsync(string title, string content = "", NodeId? parentId = null, CancellationToken cancellationToken = default);
    Task UpdateNodeContentAsync(NodeId nodeId, string content, CancellationToken cancellationToken = default);
    Task DeleteNodeAsync(NodeId nodeId, bool recursive = false, CancellationToken cancellationToken = default);
    Task MoveNodeAsync(NodeId nodeId, NodeId? newParentId, CancellationToken cancellationToken = default);
    
    // 暂不实现：关系管理、批量操作、节点分割合并
}
```

## 🖥️ CLI工具设计

### 工作空间管理命令

#### `memotree init`
**功能**: 在当前目录创建`.memotree/`工作空间
**行为**: 类似`git init`，创建本地工作空间
**输出**: 确认信息和工作空间路径

#### `memotree connect <workspace-path>`
**功能**: 创建指向远程工作空间的软链接
**行为**: 在当前目录创建`.memotree/`，内部指向`<workspace-path>`
**用途**: 跨项目共享认知仓库，解决认知连续性问题

### 内容操作命令

#### `memotree create <title> [parent-id]`
**功能**: 创建新节点
**交互**: 创建后进入内容编辑模式（类似git commit message编辑）
**输出**: 新创建的node-id
**支持**: 管道输入 `echo "内容" | memotree create "标题"`

#### `memotree expand/collapse <node-ref>`
**功能**: 展开或折叠指定节点
**node-ref支持**:
- `abc123def` - 直接使用node-id
- `"项目架构设计"` - 使用完整title（需唯一）
**输出**: 被操作节点的当前内容（按操作后状态渲染）

### 查看命令

#### `memotree` (默认命令)
**功能**: 渲染当前视图为Markdown
**输出格式**:
```markdown
# MemoTree认知空间 [root] (3/15 nodes expanded, 1.2K chars)

## 🧠 核心概念 [concept-core] 
   └── 认知节点设计 [concept-node] (展开+180 chars)
   └── LOD层次系统 [concept-lod] (展开+220 chars)

## 🏗️ 架构设计 [arch] [EXPANDED]
   MemoTree采用分层架构，从基础设施到企业特性共5个阶段...
   
   └── 存储抽象层 [arch-storage] (展开+340 chars)
   └── 业务服务层 [arch-service] (展开+280 chars)
```

#### `memotree --stats`
**功能**: 显示工作空间统计信息
**输出**: 总节点数、展开节点数、字符使用量等

## 🚀 项目结构规划

### 新增项目组织
```
src/
├── MemoTree.Core/           # 已有：核心类型和存储
├── MemoTree.Services/       # 新增：业务服务实现
│   ├── MemoTreeService.cs
│   ├── MemoTreeEditor.cs
│   ├── Models/
│   │   ├── MemoTreeViewState.cs
│   │   └── ViewStats.cs
│   └── ServiceCollectionExtensions.cs
└── MemoTree.Cli/           # 新增：CLI工具
    ├── Program.cs
    ├── Commands/
    │   ├── InitCommand.cs
    │   ├── ConnectCommand.cs
    │   ├── CreateCommand.cs
    │   ├── ExpandCommand.cs
    │   ├── CollapseCommand.cs
    │   └── RenderCommand.cs
    ├── Services/
    │   └── WorkspaceManager.cs
    └── MemoTree.Cli.csproj
```

## 📋 实施优先级

### P0 (正在实现中)
1. **创建MemoTree.Services项目**
2. **实现MemoTree.Core.Types.CognitiveNode的基础部分和相关模型**
3. **实现简化版IMemoTreeService**
4. **创建CLI项目框架**
5. **实现init和create命令**

### P1 (下次会话)
1. **完善expand/collapse功能**
2. **实现默认渲染命令**
3. **添加统计信息功能**
4. **完善错误处理**

### P2 (后续迭代)
1. **connect命令实现**
2. **title引用支持**
3. **路径式引用**
4. **通配符和正则表达式支持**

## 🎯 成功标准

### MVP完成标志
- [ ] 能够创建和管理节点层次结构
- [ ] 支持展开/折叠操作
- [ ] 能够渲染为可读的Markdown
- [ ] CLI工具可以进行基础操作
- [ ] 刘德智能够用它管理认知文件

### 长期愿景
- **认知迁移**: 将现有认知文件整理成MemoTree结构
- **LLM集成**: 与LLM Context无缝整合
- **动态上下文**: 根据对话内容动态展开相关节点
- **知识进化**: 支持认知结构的持续更新和完善

---

**下一步行动**: 开始实现MemoTree.Services项目和基础CLI工具
