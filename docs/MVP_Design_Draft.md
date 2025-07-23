# MemoTree MVP 设计草稿

> 基于 2025年7月23日 讨论整理
> 本文档整理了MemoTree项目的MVP阶段技术实现方案和设计决策

## 项目概述

MemoTree是一个为LLM提供持久化、结构化上下文管理的工具。它将LLM的上下文抽象为一个可展开/折叠的多级LOD Markdown树，支持版本控制、检索和编辑功能，解决LLM在处理复杂长周期任务时的上下文管理问题。

### 核心价值

- **动态上下文管理**: 提供可折叠/展开的多级LOD认知节点
- **版本控制**: 基于Git的历史追踪和协作支持
- **结构化思考**: 将信息组织为层次化的知识图谱
- **扩展性**: 支持代码重构(Roslyn)、环境信息等数据源接入

## 技术架构设计

### 1. 磁盘存储层 (Disk Storage Layer)

#### 1.1 Workspace 结构
```
Workspace/
├── .git/                    # Git版本控制
├── CogNodes/               # 认知节点存储根目录
│   ├── node-001/          # 单个认知节点文件夹
│   │   ├── meta.yaml      # 元数据(父节点、哈希等)
│   │   ├── brief.md       # 简要内容
│   │   ├── summary.md     # 摘要内容
│   │   └── full-text.md   # 完整内容
│   └── node-002/
├── Relations/              # 节点关系集中存储目录
│   ├── relations.yaml     # 关系数据文件
│   └── relation-types.yaml # 关系类型定义
├── last-view.json         # 最近渲染视图状态
└── index-cache.json       # 可重建的索引缓存
```

#### 1.2 设计决策

**文件夹作为认知节点存储单元**
- ✅ 与Git天然协同，支持diff、merge等操作
- ✅ 人类可直接通过文件浏览器查看和理解
- ✅ 便于调试和维护

**LOD分离存储**
- `meta.yaml`: 选择YAML格式提升可读性，支持注释
- `brief.md` / `summary.md` / `full-text.md`: 按需加载特定LOD，提升内存效率

**元数据设计示例**
```yaml
# meta.yaml
node_id: "node-001"
parent_id: "node-root"
created_at: "2025-07-23T10:30:00Z"
last_modified: "2025-07-23T11:15:00Z"
content_hashes:
  brief: "sha256:abc123..."
  summary: "sha256:def456..."
  full_text: "sha256:ghi789..."
tags: ["architecture", "dependency-injection"]
```

**关系数据集中存储设计**
```yaml
# Relations/relations.yaml
relations:
  - id: "rel-001"
    source_id: "node-001"
    target_id: "node-015"
    type: "references"
    description: "引用了DI优点分析"
    created_at: "2025-07-23T10:30:00Z"
  - id: "rel-002"
    source_id: "node-001"
    target_id: "node-008"
    type: "inspired_by"
    description: "基于架构讨论得出"
    created_at: "2025-07-23T10:30:00Z"

# Relations/relation-types.yaml
relation_types:
  references:
    description: "引用关系"
    bidirectional: false
    color: "#3498db"
  inspired_by:
    description: "启发关系"
    bidirectional: false
    color: "#e74c3c"
  contradicts:
    description: "矛盾关系"
    bidirectional: false
    color: "#f39c12"
  extends:
    description: "扩展关系"
    bidirectional: false
    color: "#2ecc71"
```

**关系数据集中存储的设计理念**
- **数据分离**: 将关系数据从节点元数据中分离，实现关注点分离
- **查询优化**: 集中存储便于实现高效的图遍历算法和关系查询
- **扩展性**: 支持复杂关系类型定义和关系属性扩展
- **一致性**: 统一的关系管理避免数据冗余和不一致问题

**扩展性考虑**
- 未来支持分片: `CogNodes-01/`, `CogNodes-02/` 避免单目录文件过多
- 关系数据分片: `Relations-01/`, `Relations-02/` 支持大规模关系数据
- 多模态支持: 采用"数据结构分离"方案，媒体文件独立存储，索引串联

### 2. 内存层 (Memory Layer)

#### 2.1 常驻进程架构
- 持续运行的服务进程，维护虚拟Markdown树的内存表示
- 磁盘存储与内存结构的映射管理
- 高性能的节点访问和更新操作

#### 2.2 检索系统
- **全文搜索**: 基于关键字的精确查找 (可考虑集成 `Lucene.net`)
- **语义检索**: 基于向量的模糊联想 (可考虑 `Faiss`、`HNSW` 等)
- **关系检索**: 基于集中存储的关系数据进行图遍历和关系查询

### 3. 基础设施

#### 3.1 MVP阶段简化策略
- **LLM支持**: 仅支持 Google Gemini API，避免过早抽象
- **多模态**: MVP阶段仅支持纯文本，验证核心功能
- **部署**: 本地进程，无需分布式架构

#### 3.2 开发工具
- **交互GUI**: 提供与LLM等价的可视化操作界面
- 支持节点的查看、编辑、展开/折叠操作
- 可视化节点关系和历史变更

## 依赖注入架构决策

### 为什么从一开始就引入DI

基于项目复杂度和长期维护考虑，**强烈建议从MVP阶段就引入依赖注入**。

#### 优点分析
1. **模块解耦**: `CanvasEditor`、`AttentionDrivenLOD`、Git服务等模块间松耦合
2. **可测试性**: 可用Mock对象替换真实依赖，实现快速独立测试
3. **扩展性**: 未来支持多种LLM、版本控制系统、代码解析器时变更成本低
4. **并行开发**: 团队可基于接口约定并行开发不同模块

#### 代价权衡
1. **学习曲线**: 对于复杂项目，DI复杂度相对可控
2. **调试复杂性**: 现代IDE对DI支持良好，单元测试可覆盖配置问题
3. **性能开销**: 主要在启动时，相比LLM推理、文件IO开销微不足道

#### 推荐方案
使用 .NET 内置的 `Microsoft.Extensions.DependencyInjection`，配置生命周期管理。

## LLM用户体验设计

### 核心交互模式

#### 上下文管理
- 提供工具调用实现节点的展开/折叠操作
- FIFO机制管理有限上下文窗口
- 支持基于任务的多视图切换 (未来扩展)

#### 版本控制集成
- 每个操作可选择性地映射为Git commit
- API设计示例: `canvas.add_node(parent, content, commit_message="Add DI analysis")`
- 支持历史回滚和分支管理

#### 异步处理
- 后台小型LLM自动生成LOD内容
- 非阻塞式操作，完成后通过通知机制告知主LLM
- 支持批量操作和增量更新

### 扩展功能 (后续版本)

#### 多视图支持
```
views/
├── coding-view.json      # 代码开发视角
├── docs-writing-view.json # 文档撰写视角
└── research-view.json    # 研究分析视角
```

#### 关系图谱
- 节点间支持多种关系类型: `references`, `inspired_by`, `contradicts`, `extends`
- 关系数据集中存储，支持高效的图遍历和复杂查询
- 可视化知识图谱导航
- 基于关系的智能推荐和路径发现

## 实施计划

### 第一阶段: 核心存储和基础架构
1. 实现文件系统存储层
2. 设计并实现DI容器配置
3. 创建基础的认知节点CRUD操作
4. 集成Git版本控制

### 第二阶段: LLM交互接口
1. 设计工具调用API
2. 实现上下文视图管理
3. 开发检索功能(关键字+向量)
4. **实现关系数据集中存储和管理系统**
5. **开发多视图支持基础架构**
6. 创建开发用GUI工具

### 第三阶段: 智能化功能
1. 集成后台LOD生成
2. **完善关系图谱可视化和智能推荐**
3. **实现基于关系的高级查询功能**
4. 添加环境信息数据源
5. 优化性能和用户体验

### 第四阶段: 智能化功能
1. 实现Roslyn代码分析集成
2. 实现Roslyn代码重构集成

### 第五阶段: 扩展和优化
1. 多模态支持
2. 多LLM兼容性
3. 分布式架构支持
4. 商业化功能包装

## 技术选型建议

### 必需依赖
- **存储**: 文件系统 + Git (LibGit2Sharp)
- **DI容器**: Microsoft.Extensions.DependencyInjection
- **元数据格式**: YAML (YamlDotNet)
- **GUI框架**: WPF/Avalonia/MAUI (待定)

### 可选增强
- **全文搜索**: Lucene.Net
- **向量检索**: Microsoft.ML / Faiss集成
- **配置管理**: Microsoft.Extensions.Configuration
- **日志记录**: Serilog

## 风险评估与缓解

### 主要风险
1. **文件系统性能**: 大量小文件可能影响性能
   - 缓解: 分片存储 + 内存缓存
2. **Git仓库膨胀**: 大量commit可能导致仓库过大
   - 缓解: 定期压缩 + 可选的归档策略
3. **内存占用**: 大型知识图谱的内存消耗
   - 缓解: 惰性加载 + LRU缓存策略

### 技术债务预防
- 从MVP开始就建立完善的单元测试
- 使用接口抽象隔离外部依赖
- 定期重构和代码审查
- 详细的API文档和使用示例

---

*本文档将随着实施进展持续更新和完善。*
