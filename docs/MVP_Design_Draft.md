# MemoTree MVP 设计草稿

> 基于 2025年7月27日 最新设计更新
> 本文档整理了MemoTree项目的MVP阶段技术实现方案和设计决策
> **状态**: 已完成19个文档拆分，实现Git命令直接映射(v2.1)

## 项目概述

**MemoTree** 是一个为LLM提供持久化、结构化上下文管理的工具。它将LLM的上下文抽象为可展开/折叠的多级LOD Markdown树，支持版本控制、检索和编辑功能。

### 核心价值主张
- **动态上下文管理**: 可折叠/展开的多级LOD认知节点
- **版本控制**: 基于Git的历史追踪和协作支持
- **结构化思考**: 层次化的知识图谱组织
- **扩展性**: 支持Roslyn代码分析、环境信息等数据源

### 🚀 内存优先架构 (v1.1)
- **常驻内存**: 所有已加载节点保持在内存中，实现零延迟访问
- **同步落盘**: 写操作立即持久化，确保数据一致性
- **简化设计**: 移除复杂缓存层，专注核心功能
- **现代硬件友好**: 充分利用现代机器的大内存优势

### 🎯 Git风格工作流 (v2.1)
- **编辑操作**: 落盘但不自动commit，类似Git工作区
- **Git命令映射**: API直接映射Git命令(GitStatus/GitDiff/GitCommit/GitCheckout)
- **LLM友好**: 将文件路径映射为GUID、文件内容映射为Markdown节点
- **变更管理**: 支持选择性提交和变更管理

## 🏗️ 系统架构设计

MemoTree采用分层架构，从基础设施到企业特性共5个阶段：

### Phase1: 基础设施层 (Foundation)
- 核心数据类型 (NodeId, CognitiveNode, LOD级别)
- 约束验证系统 (NodeConstraints, ValidationRules)
- 异常处理体系 (MemoTreeException层次，Fast Fail策略)
- 配置管理 (MemoTreeOptions, 存储配置)

### Phase2: 存储抽象层 (Storage)
- 基础存储接口 (元数据、内容、复合存储)
- 关系存储 (语义关系、层次结构、关系类型)
- 视图状态存储 (缓存策略、节点缓存)

### Phase3: 业务服务层 (Services)
- 核心服务 (MemoTree核心服务、LOD生成、环境信息)
- 关系服务 (关系管理、图遍历、路径查找)
- 编辑服务 (MemoTree编辑器、内容生成、事件系统)
- 检索服务 (多模式搜索、索引管理)

### Phase4: 集成接口层 (Integration)
- LLM工具调用API (请求响应、搜索功能)
- 外部数据源集成 (Roslyn分析、Agent环境)
- 版本控制集成 (Git操作、提交管理)

### Phase5: 企业特性层 (Enterprise)
- 安全权限管理 (RBAC、审计日志、安全策略)
- 事件驱动架构 (发布订阅、异步处理)
- 性能优化 (多层缓存、监控系统)
- 插件扩展系统 (插件接口、扩展点)
- 工厂构建器模式 (对象创建、流畅API)

## 技术架构详细设计

### 1. 磁盘存储层 (Disk Storage Layer)

#### 1.1 Workspace 结构
```
Workspace/
├── .git/                    # Git版本控制
├── CogNodes/               # 认知节点存储根目录
│   ├── node-001/          # 单个认知节点文件夹
│   │   ├── meta.yaml      # 元数据(哈希、标签等，不含parent_id)
│   │   ├── brief.md       # 简要内容
│   │   ├── summary.md     # 摘要内容
│   │   └── detail.md   # 完整内容
│   └── node-002/
├── Hierarchy/        # 父子关系集中存储目录
│   ├── node-root.yaml     # 根节点的子节点列表
│   ├── node-001.yaml      # node-001的子节点列表
│   └── node-002.yaml      # node-002的子节点列表(如果有子节点)
├── Relations/              # 其他节点关系集中存储目录
│   ├── relations.yaml     # 关系数据文件(引用、启发等)
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
- `brief.md` / `summary.md` / `detail.md`: 按需加载特定LOD，提升内存效率

**🆔 GUID编码优化 (v1.5)**
- **NodeId**: 使用GuidEncoder.ToIdString生成，**推荐Base4096-CJK编码(11字符)**
- **根节点ID**: 使用Guid.Empty编码，经 GuidEncoder.ToIdString 输出（当前默认 Base4096‑CJK；兼容 Base64）
- **编码优势**: 解决Base64的文件系统兼容性、URL安全性、路径长度等问题
- **智能检索层**: 支持汉字片段精确匹配，如"一二三四"匹配完整NodeId
- **向后兼容**: 智能识别Base64和Base4096-CJK格式，平滑迁移

**元数据设计示例**
```yaml
# CogNodes/一二三四五六七八九十丁/meta.yaml
node_id: "一二三四五六七八九十丁"  # Base4096-CJK编码的GUID
title: "AI存在的意义是什么？"
created_at: "2025-07-27T10:30:00Z"
last_modified: "2025-07-27T11:15:00Z"
content_hashes:
  brief: "sha256:abc123..."
  summary: "sha256:def456..."
  detail: "sha256:ghi789..."
tags: ["architecture", "dependency-injection"]
custom_properties:
  priority: "high"
  category: "philosophy"
```

**父子关系存储设计示例**
```yaml
# Hierarchy/node-root.yaml
parent_id: "node-root"
children:
  - node_id: "node-001"
    created_at: "2025-07-23T10:30:00Z"
  - node_id: "node-002"
    created_at: "2025-07-23T10:35:00Z"
  - node_id: "node-003"
    created_at: "2025-07-23T10:40:00Z"

# Hierarchy/node-001.yaml
parent_id: "node-001"
children:
  - node_id: "node-004"
    created_at: "2025-07-23T11:00:00Z"
  - node_id: "node-005"
    created_at: "2025-07-23T11:05:00Z"
```

**其他关系数据集中存储设计**
```yaml
# Relations/relations.yaml (存储引用、启发等语义关系，不包括父子关系)
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

**父子关系独立存储的设计理念**
- **顺序保证**: 在父节点中存储有序的children列表，确保同级节点的先后顺序
- **查询效率**: 父到子的查询直接读取文件，子到父的查询通过运行时构建的只读索引完成
- **数据一致性**: 避免双向存储带来的不一致风险，采用图数据库的单向存储模式
- **关注点分离**: 将层次结构关系与语义关系分开管理，职责清晰

**其他关系数据集中存储的设计理念**
- **数据分离**: 将语义关系数据从节点元数据中分离，实现关注点分离
- **查询优化**: 集中存储便于实现高效的图遍历算法和关系查询
- **扩展性**: 支持复杂关系类型定义和关系属性扩展
- **一致性**: 统一的关系管理避免数据冗余和不一致问题

**扩展性考虑**
- 未来支持分片: `CogNodes-01/`, `CogNodes-02/` 避免单目录文件过多
- 父子关系分片: `Hierarchy-01/`, `Hierarchy-02/` 支持大规模层次结构
- 其他关系数据分片: `Relations-01/`, `Relations-02/` 支持大规模语义关系数据
- 多模态支持: 采用"数据结构分离"方案，媒体文件独立存储，索引串联

### 2. 内存优先架构 (Memory-First Layer)

#### 2.1 🚀 内存优先设计理念
- **常驻内存**: 所有已加载认知节点保持在内存中，实现零延迟访问
- **同步落盘**: 写操作立即持久化到磁盘，确保数据一致性和安全性
- **简化架构**: 移除复杂的缓存层，消除缓存一致性问题
- **现代硬件友好**: 充分利用现代机器的大内存优势，几十GB内存足以支撑大型项目

#### 2.2 技术优势
- 消除缓存一致性问题，减少30-40%相关代码
- 零缓存未命中延迟，提升用户体验
- 简化调试和测试，无缓存状态不一致问题
- 类似Redis的成熟架构模式

#### 2.3 检索系统
- **全文搜索**: 基于关键字的精确查找 (可考虑集成 `Lucene.net`)
- **语义检索**: 基于向量的模糊联想 (可考虑 `Faiss`、`HNSW` 等)
- **层次结构检索**: 基于Hierarchy文件夹的树形遍历和路径查询
- **语义关系检索**: 基于Relations文件夹的关系数据进行图遍历和关系查询
- **反向索引**: 运行时构建子节点到父节点的只读索引，支持快速反向查找
- **智能检索层**: 支持4-8字符片段精确匹配完整GUID，LLM友好交互

### 3. 基础设施与关键设计决策

#### 3.1 🎯 MVP关键架构决策

**1. Fast Fail异常处理策略**
- 所有异常直接向上传播，保持故障现场完整性
- 避免复杂的try-catch嵌套，提高代码可读性
- 延迟复杂异常处理到Phase 5企业级特性
- 使用统一的TODO标记标识后期增强点

**2. 约束验证系统**
- 硬约束(SystemLimits/NodeConstraints)优先于软约束(配置选项)
- Token限制：单个CogNode(8K) vs 整个MemoTree视图(128K-200K)
- IConfigurationValidator接口确保配置值不超过系统硬限制

**3. 类型安全优化**
- CustomPropertiesExtensions提供安全访问方法
- WithContext<T>泛型扩展方法消除类型转换风险
- 配置职责分离：MemoTreeOptions(物理布局) vs RelationOptions(行为逻辑)

#### 3.2 MVP阶段简化策略
- **LLM支持**: 仅支持 Google Gemini API，避免过早抽象
- **多模态**: MVP阶段仅支持纯文本，验证核心功能
- **部署**: 本地进程，无需分布式架构
- **Roslyn集成**: 默认关闭，符合Phase 4实施计划

#### 3.3 开发工具
- **交互GUI**: 提供与LLM等价的可视化操作界面
- 支持节点的查看、编辑、展开/折叠操作
- 可视化节点关系和历史变更

## 依赖注入架构决策

### 为什么从一开始就引入DI

基于项目复杂度和长期维护考虑，**强烈建议从MVP阶段就引入依赖注入**。

#### 优点分析
1. **模块解耦**: `MemoTreeEditor`、`AttentionDrivenLOD`、Git服务等模块间松耦合
2. **可测试性**: 可用Mock对象替换真实依赖，实现快速独立测试
3. **扩展性**: 未来支持多种LLM、版本控制系统、代码解析器时变更成本低
4. **并行开发**: 团队可基于接口约定并行开发不同模块

#### 代价权衡
1. **学习曲线**: 对于复杂项目，DI复杂度相对可控
2. **调试复杂性**: 现代IDE对DI支持良好，单元测试可覆盖配置问题
3. **性能开销**: 主要在启动时，相比LLM推理、文件IO开销微不足道

#### 推荐方案
使用 .NET 内置的 `Microsoft.Extensions.DependencyInjection`，配置生命周期管理。

## 🎯 LLM用户体验设计

### 核心交互模式

#### 上下文管理
- 提供工具调用实现节点的展开/折叠操作
- FIFO机制管理有限上下文窗口
- 支持基于任务的多视图切换 (未来扩展)

#### 🔄 Git风格版本控制集成 (v2.1)
- **编辑操作**: 落盘但不自动commit，类似Git工作区概念
- **Git命令直接映射**:
  - `GitStatus()` - 查看未提交变更，等同于git status
  - `GitDiff(nodeId)` - 查看节点变更详情，等同于git diff
  - `GitCommit(message, nodeIds)` - 提交指定节点变更，等同于git commit
  - `GitCheckout(nodeId)` - 丢弃节点变更，等同于git checkout
- **LLM友好设计**: 将文件路径映射为GUID、文件内容映射为Markdown节点
- **变更管理**: 支持选择性提交和精细化变更控制

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
- **层次结构关系**: 父子关系通过Hierarchy文件夹管理，保证顺序和一致性
- **语义关系**: 支持多种关系类型如`references`, `inspired_by`, `contradicts`, `extends`
- **混合图遍历**: 同时支持树形层次遍历和语义关系图遍历
- **可视化导航**: 区分显示层次结构和语义关系的知识图谱
- **智能推荐**: 基于层次结构和语义关系的路径发现和内容推荐

## 📋 实施计划与项目状态

### 🎉 项目完成状态
- **总文档数**: 19个 (已完成100%)
- **总行数**: 7,099行 + 设计文档
- **完成状态**: 核心架构设计完成，已实现Git命令直接映射(v2.1)
- **类型索引**: 160+个类型定义，实现"一次加载，全局认知"
- **最后更新**: 2025-07-27

### Phase 1: 基础设施层 (Foundation) ✅
1. ✅ 核心数据类型 (NodeId, CognitiveNode, LOD级别)
2. ✅ 约束验证系统 (NodeConstraints, ValidationRules)
3. ✅ 异常处理体系 (MemoTreeException层次，Fast Fail策略)
4. ✅ 配置管理 (MemoTreeOptions, 存储配置)

### Phase 2: 存储抽象层 (Storage) ✅
1. ✅ 基础存储接口 (元数据、内容、复合存储)
2. ✅ 关系存储 (语义关系、层次结构、关系类型)
3. ✅ 视图状态存储 (缓存策略、节点缓存)

### Phase 3: 业务服务层 (Services) ✅
1. ✅ 核心服务 (MemoTree核心服务、LOD生成、环境信息)
2. ✅ 关系服务 (关系管理、图遍历、路径查找)
3. ✅ 编辑服务 (MemoTree编辑器、内容生成、事件系统)
4. ✅ 检索服务 (多模式搜索、索引管理)

### Phase 4: 集成接口层 (Integration) ✅
1. ✅ LLM工具调用API (请求响应、搜索功能，Git命令直接映射)
2. ✅ 外部数据源集成 (Roslyn分析、Agent环境)
3. ✅ 版本控制集成 (Git操作、提交管理)

### Phase 5: 企业特性层 (Enterprise) ✅
1. ✅ 安全权限管理 (RBAC、审计日志、安全策略)
2. ✅ 事件驱动架构 (发布订阅、异步处理)
3. ✅ 性能优化 (多层缓存、监控系统)
4. ✅ 插件扩展系统 (插件接口、扩展点)
5. ✅ 工厂构建器模式 (对象创建、流畅API)

## 🛠️ 技术选型建议

### 必需依赖
- **存储**: 文件系统 + Git (LibGit2Sharp)
- **DI容器**: Microsoft.Extensions.DependencyInjection
- **元数据格式**: YAML (YamlDotNet)
- **GUID编码**: 推荐Base4096-CJK编码(11字符)，解决文件系统兼容性问题
- **GUI框架**: WPF/Avalonia/MAUI (待定)

### 可选增强
- **全文搜索**: Lucene.Net
- **向量检索**: Microsoft.ML / Faiss集成
- **配置管理**: Microsoft.Extensions.Configuration
- **日志记录**: Serilog
- **智能检索**: 4-8字符片段精确匹配系统

## ⚠️ 风险评估与缓解

### 主要风险
1. **文件系统性能**: 大量小文件可能影响性能
   - 缓解: 分片存储 + 内存优先架构
2. **Git仓库膨胀**: 大量commit可能导致仓库过大
   - 缓解: Git风格工作流，编辑操作不自动commit
3. **内存占用**: 大型知识图谱的内存消耗
   - 缓解: 内存优先架构，现代硬件友好设计

### 技术债务预防
- 从MVP开始就建立完善的单元测试
- 使用接口抽象隔离外部依赖
- Fast Fail异常处理策略，简化代码逻辑
- 详细的API文档和使用示例

## 🎯 重要设计决策总结

### v1.1 内存优先架构
- 移除复杂缓存层，所有节点常驻内存
- 同步落盘确保数据一致性
- 消除缓存一致性问题，减少30-40%相关代码

### v1.5 GUID编码优化
- 推荐从day1采用Base4096-CJK编码(11字符)
- 解决Base64的文件系统兼容性和URL安全性问题
- 根节点ID使用Guid.Empty编码，消除Magic String
- 智能格式识别支持平滑迁移

### v2.1 Git命令直接映射
- API命名与Git命令完全一致
- 编辑操作落盘但不commit，符合Git哲学
- LLM友好的文件路径→GUID映射

---

*本文档反映MemoTree项目的最新设计状态 (2025-07-27)，已完成19个文档的完整架构设计。*
