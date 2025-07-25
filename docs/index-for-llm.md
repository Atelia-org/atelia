# MemoTree项目LLM索引

> **目的**: 为LLM提供项目整体认知的快速索引
> **更新**: 2025-07-25 (类型索引重构 - Phase1完成)
> **状态**: 🔄 索引重构中 (Phase1: 4/19 文档已重构)

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

## � 类型定义索引

> **核心价值**: 一次加载，全局认知 - LLM只需读取此索引即可建立完整的项目类型认知

### Phase1_CoreTypes.md (基础数据类型)
- `NodeId` - 认知节点的唯一标识符，支持字符串格式和相等性比较
- `RelationId` - 关系标识符，用于唯一标识节点间的语义关系
- `LodLevel` - LOD级别枚举，定义内容详细程度(Title/Brief/Detail/Full)
- `NodeType` - 认知节点类型枚举，区分概念、实体、过程、属性等节点类别
- `RelationType` - 节点间关系类型枚举，定义引用、依赖、组合等语义关系
- `NodeConstraints` - 节点约束定义类，包含ID长度、标题长度等硬限制常量

### Phase1_Constraints.md (约束验证系统)
- `INodeValidator` - 节点验证器接口，提供元数据和内容验证功能
- `IBusinessRuleValidator` - 业务规则验证器接口，验证节点创建和关系建立规则
- `NodeConstraints` - 节点约束定义类，定义节点各字段的长度和格式限制
- `SystemLimits` - 系统硬限制常量类，定义Token数量、文件大小等不可配置的上限
- `IConfigurationValidator` - 配置验证器接口，确保配置值不超过系统硬限制
- `DefaultConfigurationValidator` - 默认配置验证器实现，提供标准的配置验证逻辑

### Phase1_Exceptions.md (异常处理体系)
- `MemoTreeException` - 所有MemoTree异常的抽象基类，提供错误代码和上下文支持
- `NodeNotFoundException` - 节点不存在异常，包含未找到的节点ID信息
- `NodeContentNotFoundException` - 节点内容不存在异常，包含节点ID和LOD级别信息
- `StorageException` - 存储操作失败异常，封装底层存储错误
- `RetrievalException` - 检索操作失败异常，处理搜索和查询错误
- `VersionControlException` - 版本控制操作失败异常，处理Git相关错误
- `ExceptionHandlingStrategy` - 异常处理策略枚举，MVP阶段采用Fast Fail模式

### Phase1_Configuration.md (配置管理系统)
- `MemoTreeOptions` - 主配置类，包含工作空间路径、存储配置等核心选项
- `StorageOptions` - 存储配置选项，定义文件名、目录结构等存储相关设置
- `RelationOptions` - 关系管理配置，控制父子关系存储、关系类型管理等选项
- `RetrievalOptions` - 检索配置选项，控制全文搜索、索引策略等检索功能
- `ViewOptions` - 视图配置选项，管理视图状态存储、缓存策略等视图相关设置
- `IConfigurationValidator<T>` - 泛型配置验证器接口，提供类型安全的配置验证
- `IMemoTreeConfigurationValidator` - MemoTree专用配置验证器接口，验证各配置模块
- `NodeStorageService` - 节点存储服务示例，展示配置注入和使用模式

## �📚 文档地图 (19个文档)

### Phase 1: 基础设施 (4/4 完成)
- **Phase1_CoreTypes.md** (285行) - 核心数据类型、枚举、标识符
- **Phase1_Constraints.md** (202行) - 约束定义、验证规则、系统限制
- **Phase1_Exceptions.md** (243行) - 异常类型定义、错误处理
- **Phase1_Configuration.md** (532行) - 配置选项、系统设置，新增ViewOptions类

### Phase 2: 存储层 (3/3 完成)
- **Phase2_StorageInterfaces.md** (396行) - 存储接口定义体系（权威源）
- **Phase2_RelationStorage.md** (601行) - 关系管理服务、关系图、事件系统
- **Phase2_ViewStorage.md** (377行) - 视图状态存储、缓存策略，优化配置引用

### Phase 3: 服务层 (4/4 完成)
- **Phase3_CoreServices.md** (407行) - 核心业务服务接口
- **Phase3_RelationServices.md** (219行) - 关系管理服务接口
- **Phase3_EditingServices.md** (299行) - 编辑操作、LOD生成服务
- **Phase3_RetrievalServices.md** (155行) - 检索服务接口、多模式搜索

### Phase 4: 集成层 (3/3 完成)
- **Phase4_ToolCallAPI.md** (454行) - LLM工具调用接口、请求响应类型
- **Phase4_ExternalIntegration.md** (294行) - 外部数据源集成、Roslyn集成
- **Phase4_VersionControl.md** (229行) - Git版本控制集成、提交管理

### Phase 5: 企业特性 (5/5 完成)
- **Phase5_Security.md** (660行) - 权限管理、安全策略、审计日志
- **Phase5_EventSystem.md** (226行) - 事件系统、发布订阅机制
- **Phase5_Performance.md** (657行) - 性能优化、缓存策略、监控系统
- **Phase5_Extensions.md** (269行) - 插件系统、扩展机制、数据源插件
- **Phase5_Factories.md** (280行) - 工厂模式、构建器模式



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

### 🎯 MVP关键架构决策：Fast Fail异常处理策略

**决策背景**: 为优化LLM代码理解和维护效率，简化MVP实现复杂度
**核心原则**:
- 所有异常直接向上传播，保持故障现场完整性
- 避免复杂的try-catch嵌套，提高代码可读性
- 延迟复杂异常处理到Phase 5企业级特性
- 使用统一的TODO标记标识后期增强点

**适用范围**: Phase 1-4 MVP阶段
**后期规划**: Phase 5实现完整的异常处理、重试机制、降级策略

## 🎯 使用指南

### 基于类型索引的快速定位
- **需要特定类型**: 在上方类型索引中搜索类型名，直接定位到对应文档
- **需要某类功能**: 根据类型简介了解用途，选择相关类型和文档
- **理解依赖关系**: 通过类型简介中的关键词理解类型间的协作关系
- **实现具体功能**: 先从类型索引建立认知，再查看具体文档获取详细定义

### 常见查找场景
- **节点操作**: 查找 `NodeId`, `CognitiveNode`, `INodeValidator` 等类型
- **存储功能**: 查找 `INodeStorage`, `ICompositeNodeStorage` 等存储接口
- **关系管理**: 查找 `RelationType`, `NodeRelation`, `IRelationService` 等关系类型
- **配置设置**: 查找 `MemoTreeOptions`, `StorageOptions` 等配置类型
- **异常处理**: 查找 `MemoTreeException` 及其派生异常类型

### 实施顺序
1. **Phase 1** → 建立基础类型和配置 (27个核心类型)
2. **Phase 2** → 实现存储抽象层 (存储接口和实现)
3. **Phase 3** → 构建核心业务服务 (业务逻辑和服务接口)
4. **Phase 4** → 集成外部系统 (LLM工具调用和外部数据源)
5. **Phase 5** → 添加企业级特性 (安全、性能、扩展性)

## 📋 项目状态

- **总文档数**: 19个
- **总行数**: 6,627行
- **完成状态**: 🎉 100% 完成
- **最后更新**: 2025-07-25
- **原始文档**: Core_Types_Design.md (3116行) → 成功拆分成若干Phase_XXX.md。已删除，git中有旧档。
- **重要决策**:
  - 基于Review建议优化配置一致性 (TitleContentFileName→BriefContentFileName)
  - 确定MVP Fast Fail异常处理策略，优化LLM代码理解和维护效率
  - Roslyn集成默认关闭，符合Phase 4实施计划
  - 实施约束层次验证机制：硬约束(SystemLimits/NodeConstraints) > 软约束(配置选项)
  - 澄清Token限制：单个CogNode(8K) vs 整个MemoTree视图(128K-200K)
  - **接口定义整合**: 将重复的存储接口定义整合到Phase2_StorageInterfaces.md，确保单一数据源原则
  - **配置选项归属优化**: 新增ViewOptions配置类，明确视图相关配置归属，优化文档间类型引用

## 🔍 快速搜索提示

当你需要具体信息时，推荐流程：
1. **查看类型索引** - 在上方类型索引中搜索相关类型名或关键词
2. **理解类型用途** - 通过1句话简介快速理解类型的核心作用
3. **定位源文档** - 根据类型归属直接跳转到对应的PhaseN_XXX.md文档
4. **获取详细定义** - 在源文档中查看完整的类型定义、方法和使用示例

### 搜索技巧
- **按功能搜索**: 如搜索"存储"找到所有Storage相关类型
- **按层次搜索**: 如搜索"Service"找到所有服务接口
- **按阶段搜索**: 直接查看特定Phase的类型列表

---
**维护说明**:
- **类型索引**: 新增类型时，只需在对应Phase部分添加类型条目，保持1对1维护关系
- **架构更新**: 架构变更时更新系统架构骨干部分，类型索引自动反映具体变化
- **简化维护**: 无需维护文档间依赖关系，依赖信息由各源文档自行管理
