# MemoTree项目LLM索引

> **目的**: 为LLM提供项目整体认知的快速索引  
> **更新**: 2025-07-25 <后续修改时请添加分钟级时间>  
> **状态**: 🎉 项目完成 (19/19 文档)  

## 🎯 项目核心概念

**MemoTree** 是一个为LLM提供持久化、结构化上下文管理的工具。它将LLM的上下文抽象为可展开/折叠的多级LOD Markdown树，支持版本控制、检索和编辑功能。

### 核心价值主张
- **动态上下文管理**: 可折叠/展开的多级LOD认知节点
- **版本控制**: 基于Git的历史追踪和协作支持  
- **结构化思考**: 层次化的知识图谱组织
- **扩展性**: 支持Roslyn代码分析、环境信息等数据源

## 🏗️ 系统架构骨干

```
MemoTree系统架构
├── Phase1: 基础设施层 (Foundation)
│   ├── 核心数据类型 (NodeId, CognitiveNode, LOD级别)
│   ├── 约束验证系统 (NodeConstraints, ValidationRules)
│   ├── 异常处理体系 (MemoTreeException层次)
│   └── 配置管理 (MemoTreeOptions, 存储配置)
│
├── Phase2: 存储抽象层 (Storage)
│   ├── 基础存储接口 (元数据、内容、复合存储)
│   ├── 关系存储 (语义关系、层次结构、关系类型)
│   └── 视图状态存储 (缓存策略、节点缓存)
│
├── Phase3: 业务服务层 (Services)
│   ├── 核心服务 (认知画布、LOD生成、环境信息)
│   ├── 关系服务 (关系管理、图遍历、路径查找)
│   ├── 编辑服务 (画布编辑器、内容生成、事件系统)
│   └── 检索服务 (多模式搜索、索引管理)
│
├── Phase4: 集成接口层 (Integration)
│   ├── LLM工具调用API (请求响应、搜索功能)
│   ├── 外部数据源集成 (Roslyn分析、Agent环境)
│   └── 版本控制集成 (Git操作、提交管理)
│
└── Phase5: 企业特性层 (Enterprise)
    ├── 安全权限管理 (RBAC、审计日志、安全策略)
    ├── 事件驱动架构 (发布订阅、异步处理)
    ├── 性能优化 (多层缓存、监控系统)
    ├── 插件扩展系统 (插件接口、扩展点)
    └── 工厂构建器模式 (对象创建、流畅API)
```

## 📚 文档地图 (19个文档)

### Phase 1: 基础设施 (4/4 完成)
- **Phase1_CoreTypes.md** (300行) - 核心数据类型、枚举、标识符
- **Phase1_Constraints.md** (220行) - 约束定义、验证规则、系统限制  
- **Phase1_Exceptions.md** (250行) - 异常类型定义、错误处理
- **Phase1_Configuration.md** (440行) - 配置选项、系统设置

### Phase 2: 存储层 (3/3 完成)
- **Phase2_StorageInterfaces.md** (395行) - 存储接口定义体系
- **Phase2_RelationStorage.md** (553行) - 关系存储实现
- **Phase2_ViewStorage.md** (300行) - 视图状态存储、缓存策略

### Phase 3: 服务层 (4/4 完成)  
- **Phase3_CoreServices.md** (479行) - 核心业务服务接口
- **Phase3_RelationServices.md** (300行) - 关系管理服务接口
- **Phase3_EditingServices.md** (300行) - 编辑操作、LOD生成服务
- **Phase3_RetrievalServices.md** (180行) - 检索服务接口、多模式搜索

### Phase 4: 集成层 (3/3 完成)
- **Phase4_ToolCallAPI.md** (400行) - LLM工具调用接口、请求响应类型
- **Phase4_ExternalIntegration.md** (300行) - 外部数据源集成、Roslyn集成  
- **Phase4_VersionControl.md** (280行) - Git版本控制集成、提交管理

### Phase 5: 企业特性 (5/5 完成)
- **Phase5_Security.md** (762行) - 权限管理、安全策略、审计日志
- **Phase5_EventSystem.md** (300行) - 事件系统、发布订阅机制
- **Phase5_Performance.md** (801行) - 性能优化、缓存策略、监控系统
- **Phase5_Extensions.md** (300行) - 插件系统、扩展机制、数据源插件
- **Phase5_Factories.md** (300行) - 工厂模式、构建器模式

## 🔗 关键依赖关系

```
依赖关系图:
Phase1_CoreTypes.md (基础)
├── Phase2_StorageInterfaces.md
├── Phase3_CoreServices.md  
├── Phase4_ToolCallAPI.md
└── Phase5_* (所有Phase5文档)

Phase1_Exceptions.md
└── (被所有其他文档引用)

Phase2_StorageInterfaces.md
├── Phase2_RelationStorage.md
├── Phase2_ViewStorage.md
├── Phase3_RelationServices.md
└── Phase3_RetrievalServices.md

Phase3_CoreServices.md
├── Phase3_EditingServices.md
└── Phase4_ToolCallAPI.md
```

## 💡 核心设计模式

### 数据流向
1. **认知节点** → 多级LOD内容 (Title/Summary/Detail)
2. **存储抽象** → 元数据/内容/关系分离存储
3. **服务编排** → 认知画布 ↔ 编辑服务 ↔ 检索服务
4. **工具调用** → LLM ↔ MemoTree API ↔ 外部数据源

### 关键抽象
- **CognitiveNode**: 核心认知节点，包含元数据和多级LOD内容
- **NodeRelation**: 语义关系，支持多种关系类型和属性
- **ICognitiveCanvasService**: 认知画布服务，核心交互接口
- **ILodGenerationService**: LOD生成服务，异步内容生成

## 🎯 使用指南

### 快速定位
- **需要基础类型**: 查看 Phase1_CoreTypes.md
- **需要存储接口**: 查看 Phase2_StorageInterfaces.md  
- **需要业务逻辑**: 查看 Phase3_CoreServices.md
- **需要LLM集成**: 查看 Phase4_ToolCallAPI.md
- **需要企业特性**: 查看 Phase5_Security.md 等

### 实施顺序
1. **Phase 1** → 建立基础类型和配置
2. **Phase 2** → 实现存储抽象层
3. **Phase 3** → 构建核心业务服务  
4. **Phase 4** → 集成外部系统
5. **Phase 5** → 添加企业级特性

## 📋 项目状态

- **总文档数**: 19个
- **总行数**: ~6,500行  
- **完成状态**: 🎉 100% 完成
- **最后更新**: 2025-07-25
- **原始文档**: Core_Types_Design.md (3116行) → 成功拆分

## 🔍 快速搜索提示

当你需要具体信息时，可以：
1. **先查看此索引** - 建立整体认知
2. **定位相关Phase** - 确定具体文档
3. **查看具体文档** - 获取详细实现
4. **检查依赖关系** - 确保完整理解

---
**维护说明**: 此索引应随项目演进持续更新，始终反映最新的架构状态和文档结构。
