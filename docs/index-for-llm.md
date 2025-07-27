# MemoTree项目LLM索引

> **目的**: 为LLM提供项目整体认知的快速索引
> **更新**: 2025-07-27 (CustomProperties类型安全优化)
> **状态**: 正在逐项处理设计Review中得到的反馈

## 🎯 项目核心概念

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

## 🏗️ 系统架构与类型索引

> **核心价值**: 一次加载，全局认知 - LLM只需读取此索引即可建立完整的项目类型认知
### Phase1: 基础设施层 (Foundation)
- 核心数据类型 (NodeId, CognitiveNode, LOD级别)
- 约束验证系统 (NodeConstraints, ValidationRules)
- 异常处理体系 (MemoTreeException层次)
- 配置管理 (MemoTreeOptions, 存储配置)

#### Phase1_CoreTypes.md (基础数据类型)
(285行) 核心数据类型、枚举、标识符
- `GuidEncoder` - 统一GUID编码工具类，提供ToIdString抽象接口，当前使用Base64编码(22字符)，支持未来切换到Base4096-CJK(11字符)
- `GuidEncodingType` - GUID编码类型枚举，支持Base64、Base4096-CJK和旧格式检测
- `NodeId` - 认知节点的唯一标识符，使用GuidEncoder.ToIdString生成，支持字符串格式和相等性比较
- `RelationId` - 关系标识符，使用GuidEncoder.ToIdString生成，用于唯一标识节点间的语义关系
- `LodLevel` - LOD级别枚举，定义内容详细程度(Title/Brief/Detail/Full)
- `NodeType` - 认知节点类型枚举，区分概念、实体、过程、属性等节点类别
- `RelationType` - 节点间关系类型枚举，定义引用、依赖、组合等语义关系
- `NodeConstraints` - 节点约束定义类，包含ID长度、标题长度等硬限制常量

#### Phase1_Constraints.md (约束验证系统)
(202行) 约束定义、验证规则、系统限制
- `INodeValidator` - 节点验证器接口，提供元数据和内容验证功能
- `IBusinessRuleValidator` - 业务规则验证器接口，验证节点创建和关系建立规则
- `NodeConstraints` - 节点约束定义类，定义节点各字段的长度和格式限制
- `SystemLimits` - 系统硬限制常量类，定义Token数量、文件大小等不可配置的上限
- `IConfigurationValidator` - 配置验证器接口，确保配置值不超过系统硬限制
- `DefaultConfigurationValidator` - 默认配置验证器实现，提供标准的配置验证逻辑

#### Phase1_Exceptions.md (异常处理体系)
(243行) 异常类型定义、错误处理
- `MemoTreeException` - 所有MemoTree异常的抽象基类，提供错误代码和上下文支持
- `NodeNotFoundException` - 节点不存在异常，包含未找到的节点ID信息
- `NodeContentNotFoundException` - 节点内容不存在异常，包含节点ID和LOD级别信息
- `StorageException` - 存储操作失败异常，封装底层存储错误
- `RetrievalException` - 检索操作失败异常，处理搜索和查询错误
- `VersionControlException` - 版本控制操作失败异常，处理Git相关错误
- `ExceptionHandlingStrategy` - 异常处理策略枚举，MVP阶段采用Fast Fail模式

#### Phase1_Configuration.md (配置管理系统)
(532行) 配置选项、系统设置，新增ViewOptions类
- `MemoTreeOptions` - 主配置类，包含工作空间路径、存储配置等核心选项
- `StorageOptions` - 存储配置选项，定义文件名、目录结构等存储相关设置
- `RelationOptions` - 关系管理配置，控制父子关系存储、关系类型管理等选项
- `RetrievalOptions` - 检索配置选项，控制全文搜索、索引策略等检索功能
- `ViewOptions` - 视图配置选项，管理视图状态存储、缓存策略等视图相关设置
- `IConfigurationValidator<T>` - 泛型配置验证器接口，提供类型安全的配置验证
- `IMemoTreeConfigurationValidator` - MemoTree专用配置验证器接口，验证各配置模块
- `NodeStorageService` - 节点存储服务示例，展示配置注入和使用模式

### Phase2: 存储抽象层 (Storage)
- 基础存储接口 (元数据、内容、复合存储)
- 关系存储 (语义关系、层次结构、关系类型)
- 视图状态存储 (缓存策略、节点缓存)
 
#### Phase2_StorageInterfaces.md (存储接口定义)
(396行) - 存储接口定义体系（权威源）
- `INodeMetadataStorage` - 节点元数据存储接口，提供节点基础信息的CRUD操作
- `INodeContentStorage` - 节点内容存储接口，支持多级LOD内容的存储和检索
- `INodeRelationStorage` - 语义关系存储接口，管理节点间的语义关系数据
- `IRelationTypeStorage` - 关系类型定义存储接口，管理关系类型的元数据
- `INodeHierarchyStorage` - 节点层次结构存储接口，基于父子关系的独立存储
- `ICognitiveNodeStorage` - 复合存储接口，组合所有存储功能提供统一访问
- `IViewStateStorage` - 视图状态存储接口，管理认知画布的视图状态持久化

#### Phase2_RelationStorage.md (关系管理服务)
(601行) - 关系管理服务、关系图、事件系统
- `IRelationManagementService` - 关系管理服务接口，提供关系图构建和路径查找功能
- `RelationGraph` - 关系图数据结构，表示节点间的关系网络和连接信息
- `RelationPath` - 关系路径数据结构，描述节点间的连接路径和路径属性
- `RelationStatistics` - 关系统计信息，提供关系数量、类型分布等统计数据
- `NodeRelationChangedEvent` - 节点关系变更事件，支持关系变更的事件通知
- `RelationChangeType` - 关系变更类型枚举，定义创建、更新、删除等变更类型
- `PathType` - 路径类型枚举，区分直接路径、间接路径等不同路径类型
- `CreateRelationRequest` - 创建关系请求，封装关系创建所需的参数信息
- `RelationValidationResult` - 关系验证结果，包含验证状态和错误信息
- `RelationPatternAnalysis` - 关系模式分析结果，提供关系模式的分析和识别
- `RelationPattern` - 关系模式定义，描述常见的关系模式和规则
- `NodeHierarchyChangedEvent` - 节点层次结构变更事件，支持父子关系变更通知
- `HierarchyChangeType` - 层次结构变更类型枚举，定义添加、移除、移动等操作

#### Phase2_ViewStorage.md (视图状态存储 - 内存优先)
(405行) - 视图状态存储、内存管理，采用内存优先架构
- `NodeViewState` - 节点在视图中的状态记录，包含展开状态、可见性等属性
- `CanvasViewState` - 认知画布视图状态记录，管理整个画布的显示状态
- `IViewStateStorage` - 视图状态存储接口，提供视图状态的持久化和恢复功能
- `IViewStateMemoryManager` - 视图状态内存管理接口，提供内存使用统计和管理功能
- `MemoryUsageStats` - 内存使用统计信息记录，提供内存占用和节点数量统计
- `INodeMemoryService` - 节点内存服务接口，提供节点数据的内存管理和快速访问
- `NodeMemoryStats` - 节点内存统计信息记录，包含节点内存占用的详细统计
- `ViewOptions` - 视图配置选项类，管理视图相关的配置参数（引用自Phase1，已优化）
- `RelationOptions` - 关系配置选项类，管理关系内存相关配置（引用自Phase1，已优化）

### Phase3: 业务服务层 (Services)
- 核心服务 (认知画布、LOD生成、环境信息)
- 关系服务 (关系管理、图遍历、路径查找)
- 编辑服务 (画布编辑器、内容生成、事件系统)
- 检索服务 (多模式搜索、索引管理)

#### Phase3_CoreServices.md (核心业务服务)
(407行) 核心业务服务接口
- `ICognitiveCanvasService` - 认知画布核心服务接口，提供视图渲染和节点树构建功能
- `NodeTreeItem` - 节点树项记录，表示节点在树形结构中的显示信息
- `ILodGenerationService` - 异步LOD内容生成服务接口，支持多级内容的智能生成
- `LodGenerationRequest` - LOD生成请求记录，封装内容生成所需的参数信息
- `GenerationResult` - LOD生成结果记录，包含生成任务的状态和结果信息
- `GenerationStatus` - 生成任务状态枚举，定义等待、进行中、完成等状态
- `IRoslynIntegrationService` - Roslyn代码分析服务接口，提供代码库分析和重构功能
- `CodebaseStructure` - 代码库结构记录，描述解决方案的项目和类型组织结构
- `ProjectInfo` - 项目信息记录，包含项目名称、路径和命名空间信息
- `NamespaceInfo` - 命名空间信息记录，包含命名空间下的类型定义信息
- `TypeInfo` - 类型信息记录，描述类、接口等类型的详细信息和成员
- `MemberInfo` - 成员信息记录，描述类型成员的签名、文档等详细信息
- `TypeKind` - 类型种类枚举，区分类、接口、结构体、枚举等类型种类
- `MemberKind` - 成员种类枚举，区分字段、属性、方法、构造函数等成员类型
- `SymbolInfo` - 符号信息记录，提供代码符号的名称、类型和文档信息
- `SymbolKind` - 符号种类枚举，定义命名空间、类型、方法等符号分类
- `RefactoringOperation` - 重构操作抽象记录，定义代码重构操作的基础结构
- `RenameRefactoringOperation` - 重命名重构操作记录，封装符号重命名的具体参数
- `RefactoringResult` - 重构结果记录，包含重构操作的成功状态和变更文件信息
- `CodeChangeEvent` - 代码变更事件记录，描述文件变更和受影响的符号信息
- `ChangeType` - 变更类型枚举，定义添加、修改、删除、重命名等变更操作
- `IAgentEnvironmentService` - Agent环境信息服务接口，提供上下文使用和系统状态信息
- `ContextUsageInfo` - 上下文使用情况信息记录，包含Token使用量和活跃节点统计
- `SystemStatusInfo` - 系统状态信息记录，提供时间、位置、内存使用等系统信息
- `UserPreferences` - 用户偏好设置记录，管理默认LOD级别、自动保存等用户配置

#### Phase3_RelationServices.md (关系管理服务)
(219行) 关系管理服务接口
- `IRelationManagementService` - 关系管理服务接口，提供关系图构建和路径查找功能
- `RelationGraph` - 关系图记录，表示以中心节点为核心的关系网络结构
- `RelationPath` - 关系路径记录，描述节点间的连接路径和路径权重信息
- `RelationStatistics` - 关系统计记录，提供关系总数、类型分布等统计信息
- `RelationTypeDefinition` - 关系类型定义记录，包含关系类型的元数据和约束信息
- `RelationOptions` - 关系配置选项类，管理关系存储和缓存相关配置（引用自Phase1）

#### Phase3_EditingServices.md (编辑操作服务)
(299行) 编辑操作、LOD生成服务
- `ICognitiveCanvasEditor` - 认知画布编辑器接口，提供节点CRUD和批量操作功能
- `ILodGenerationService` - LOD生成服务接口，支持异步的多级内容生成和管理
- `LodGenerationRequest` - LOD生成请求记录，封装内容生成的输入参数和配置
- `GenerationResult` - 生成结果记录，包含生成任务的执行状态和输出内容
- `GenerationStatus` - 生成状态枚举，定义等待、进行中、完成、失败等任务状态
- `NodeChangeEvent` - 节点变更事件抽象记录，定义所有节点变更事件的基础结构
- `NodeCreatedEvent` - 节点创建事件记录，包含新创建节点的类型和层次信息
- `NodeUpdatedEvent` - 节点更新事件记录，包含内容变更前后的对比信息
- `NodeDeletedEvent` - 节点删除事件记录，包含被删除节点的基本信息和删除方式
- `NodeHierarchyChangedEvent` - 节点层次变更事件记录，包含父子关系的变更信息
- `NodeRelationChangedEvent` - 节点关系变更事件记录，包含语义关系的变更详情
- `HierarchyChangeType` - 层次变更类型枚举，定义添加、移除、移动等层次操作
- `RelationChangeType` - 关系变更类型枚举，定义创建、更新、删除等关系操作
- `IEventPublisher` - 事件发布器接口，提供事件的发布和批量发布功能
- `IEventSubscriber` - 事件订阅器接口，支持特定类型事件和全局事件的订阅

#### Phase3_RetrievalServices.md (检索服务)
(155行) 检索服务接口、多模式搜索
- `IRetrievalService` - 检索服务接口，提供多模式节点搜索和索引管理功能
- `SearchResult` - 搜索结果记录，包含匹配节点的相关性评分和匹配信息
- `SearchNodesRequest` - 节点搜索请求记录，封装搜索查询和过滤条件
- `SearchType` - 搜索类型枚举，定义全文搜索、语义搜索等不同搜索模式
- `RetrievalOptions` - 检索配置选项类，管理搜索引擎和索引相关配置（引用自Phase1）
- `RetrievalException` - 检索异常类，处理搜索和索引操作中的错误情况

### Phase4: 集成接口层 (Integration)
- LLM工具调用API (请求响应、搜索功能)
- 外部数据源集成 (Roslyn分析、Agent环境)
- 版本控制集成 (Git操作、提交管理)

#### Phase4_ToolCallAPI.md (LLM工具调用接口)
(454行) LLM工具调用接口、请求响应类型
- `ILlmToolCallService` - LLM工具调用服务接口，提供节点操作和搜索的API端点
- `ToolCallResult` - 工具调用结果记录，包含操作成功状态和返回数据
- `ExpandNodeRequest` - 展开节点请求记录，封装节点展开操作的参数
- `CollapseNodeRequest` - 折叠节点请求记录，封装节点折叠操作的参数
- `CreateNodeRequest` - 创建节点请求记录，包含新节点的类型、标题和内容信息
- `UpdateNodeRequest` - 更新节点请求记录，封装节点内容和元数据的更新参数
- `SearchNodesRequest` - 搜索节点请求记录，包含搜索查询和过滤条件
- `SearchType` - 搜索类型枚举，定义全文、语义、标签等不同搜索模式
- `SearchScope` - 搜索范围枚举，定义当前视图、全局等不同搜索范围
- `CommitChangesRequest` - 提交变更请求记录，封装Git提交操作的参数信息
- `UpdateMode` - 更新模式枚举，定义替换、追加、插入等不同更新方式
- `CommitAuthor` - 提交作者记录，包含Git提交的作者姓名和邮箱信息

#### Phase4_ExternalIntegration.md (外部数据源集成)
(294行) 外部数据源集成、Roslyn集成
- `IRoslynIntegrationService` - Roslyn集成服务接口，提供代码分析和重构功能
- `CodebaseStructure` - 代码库结构记录，描述解决方案的整体组织结构
- `ProjectInfo` - 项目信息记录，包含项目的基本信息和命名空间列表
- `NamespaceInfo` - 命名空间信息记录，包含命名空间下的类型定义
- `TypeInfo` - 类型信息记录，描述类、接口等类型的详细信息
- `MemberInfo` - 成员信息记录，描述类型成员的签名和文档信息
- `TypeKind` - 类型种类枚举，区分类、接口、结构体等不同类型
- `MemberKind` - 成员种类枚举，区分字段、属性、方法等不同成员
- `SymbolInfo` - 符号信息记录，提供代码符号的详细信息和文档
- `SymbolKind` - 符号种类枚举，定义命名空间、类型、方法等符号分类
- `RefactoringOperation` - 重构操作抽象记录，定义代码重构的基础结构
- `RenameRefactoringOperation` - 重命名重构操作记录，封装符号重命名参数
- `RefactoringResult` - 重构结果记录，包含重构操作的执行结果和变更信息
- `CodeChangeEvent` - 代码变更事件记录，描述文件变更和受影响符号
- `ChangeType` - 变更类型枚举，定义添加、修改、删除等变更操作
- `IAgentEnvironmentService` - Agent环境服务接口，提供上下文和系统状态信息
- `ContextUsageInfo` - 上下文使用信息记录，包含Token使用量和节点统计
- `SystemStatusInfo` - 系统状态信息记录，提供时间、位置、内存等系统信息
- `UserPreferences` - 用户偏好记录，管理用户的个性化配置和默认设置

#### Phase4_VersionControl.md (版本控制集成)
(229行) Git版本控制集成、提交管理
- `IVersionControlService` - 版本控制服务接口，提供Git操作和分支管理功能
- `CommitInfo` - 提交信息记录，包含Git提交的详细信息和变更统计
- `VersionControlException` - 版本控制异常类，处理Git操作中的错误情况
- `CommitChangesRequest` - 提交变更请求记录，封装Git提交的参数和选项
- `CreateNodeRequest` - 创建节点请求记录，用于版本控制上下文中的节点创建
- `UpdateNodeRequest` - 更新节点请求记录，用于版本控制上下文中的节点更新

### Phase5: 企业特性层 (Enterprise)
- 安全权限管理 (RBAC、审计日志、安全策略)
- 事件驱动架构 (发布订阅、异步处理)
- 性能优化 (多层缓存、监控系统)
- 插件扩展系统 (插件接口、扩展点)
- 工厂构建器模式 (对象创建、流畅API)

#### Phase5_Security.md (安全权限管理)
(660行) 权限管理、安全策略、审计日志
- `Permission` - 权限枚举（标志位），定义读取、写入、删除等基础权限
- `ResourceType` - 资源类型枚举，区分节点、关系、视图等不同资源类型
- `PermissionContext` - 权限上下文记录，包含用户、资源和操作的权限检查信息
- `PermissionResult` - 权限检查结果记录，包含权限状态和拒绝原因
- `IPermissionChecker` - 权限检查器接口，提供资源访问权限的验证功能
- `SecurityContext` - 安全上下文记录，包含当前用户的身份和权限信息
- `ISecurityContextProvider` - 安全上下文提供器接口，管理用户身份验证和授权
- `AuditEventType` - 审计事件类型枚举，定义登录、操作、错误等审计事件
- `AuditEvent` - 审计事件记录，包含用户操作的详细日志和上下文信息
- `AuditQuery` - 审计查询记录，封装审计日志的查询条件和过滤参数
- `IAuditLogService` - 审计日志服务接口，提供安全事件的记录和查询功能
- `SecurityOptions` - 安全配置选项类，管理权限检查、审计等安全相关配置
- `SecurityMiddleware` - 安全中间件类，提供请求级别的权限验证和审计

#### Phase5_EventSystem.md (事件驱动架构)
(226行) 事件系统、发布订阅机制
- `NodeCreatedEvent` - 节点创建事件记录，继承自NodeChangeEvent，包含创建信息
- `NodeUpdatedEvent` - 节点更新事件记录，继承自NodeChangeEvent，包含变更详情
- `NodeDeletedEvent` - 节点删除事件记录，继承自NodeChangeEvent，包含删除信息
- `NodeHierarchyChangedEvent` - 层次变更事件记录，包含父子关系的变更信息
- `NodeRelationChangedEvent` - 关系变更事件记录，包含语义关系的变更详情
- `HierarchyChangeType` - 层次变更类型枚举，定义添加、移除、移动等操作
- `RelationChangeType` - 关系变更类型枚举，定义创建、更新、删除等操作
- `IEventPublisher` - 事件发布器接口，提供异步事件发布和批量发布功能
- `IEventSubscriber` - 事件订阅器接口，支持类型化事件订阅和全局事件监听

#### Phase5_Performance.md (性能优化监控 - 内存优先)
(807行) 性能优化、内存管理、监控系统，适配内存优先架构
- `ISystemMemoryManager` - 系统内存管理器接口，提供内存使用监控和管理功能
- `SystemMemoryStats` - 系统内存统计记录，提供内存使用量、压力级别等系统指标
- `MemoryPressureLevel` - 内存压力级别枚举，定义低、中、高、临界等内存压力状态
- `MetricType` - 指标类型枚举，定义响应时间、吞吐量等不同性能指标
- `PerformanceMetric` - 性能指标记录，包含指标值、时间戳和标签信息
- `SystemPerformanceStats` - 系统性能统计记录，提供CPU、内存、磁盘等系统指标
- `IPerformanceMonitoringService` - 性能监控服务接口，提供指标收集和分析功能
- `NodeService` - 节点服务示例类，展示内存优先架构的服务集成模式
- `SearchService` - 搜索服务示例类，展示性能监控的集成和使用模式

#### Phase5_Extensions.md (插件扩展系统)
(269行) 插件系统、扩展机制、数据源插件
- `IMemoTreePlugin` - MemoTree插件基础接口，定义插件的生命周期和基本功能
- `IDataSourcePlugin` - 数据源插件接口，继承自IMemoTreePlugin，提供外部数据集成
- `DataSourceInfo` - 数据源信息记录，描述数据源的类型、连接和配置信息
- `SyncResult` - 同步结果记录，包含数据同步操作的状态和统计信息
- `DataChangeEvent` - 数据变更事件记录，描述外部数据源的变更通知
- `IPluginManager` - 插件管理器接口，提供插件的加载、卸载和生命周期管理
- `IPluginDiscoveryService` - 插件发现服务接口，负责插件的自动发现和注册
- `PluginInfo` - 插件信息记录，包含插件的元数据、版本和依赖信息
- `PluginValidationResult` - 插件验证结果记录，包含插件有效性检查的结果
- `IExtensionPoint<T>` - 泛型扩展点接口，定义系统扩展点的标准规范
- `INodeProcessingExtension` - 节点处理扩展接口，提供节点处理流程的扩展能力

#### Phase5_Factories.md (工厂构建器模式)
(280行) 工厂模式、构建器模式
- `ICognitiveNodeFactory` - 认知节点工厂接口，提供节点创建的标准化工厂方法
- `ICognitiveNodeBuilder` - 认知节点构建器接口，提供流畅API风格的节点构建功能


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

### 🎯 MVP关键架构决策

#### 1. Fast Fail异常处理策略

**决策背景**: 为优化LLM代码理解和维护效率，简化MVP实现复杂度
**核心原则**:
- 所有异常直接向上传播，保持故障现场完整性
- 避免复杂的try-catch嵌套，提高代码可读性
- 延迟复杂异常处理到Phase 5企业级特性
- 使用统一的TODO标记标识后期增强点

**适用范围**: Phase 1-4 MVP阶段
**后期规划**: Phase 5实现完整的异常处理、重试机制、降级策略

#### 2. 内存优先存储架构 (v1.1新增)

**决策背景**: 简化MVP阶段工程复杂度，充分利用现代硬件优势
**核心原则**:
- 所有已加载认知节点常驻内存，实现零延迟访问
- 写操作同步落盘，确保数据一致性和安全性
- 移除复杂的缓存层，简化存储接口和实现
- 现代硬件友好，几十GB内存足以支撑大型项目

**技术优势**:
- 消除缓存一致性问题，减少30-40%相关代码
- 零缓存未命中延迟，提升用户体验
- 简化调试和测试，无缓存状态不一致问题
- 类似Redis的成熟架构模式

**适用范围**: Phase 1-4 MVP阶段
**后期规划**: Phase 5可选添加内存管理、冷数据卸载等高级功能

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

- **总文档数**: 22个 (新增GuidEncoder接口化重构)
- **总行数**: 6,627行 + 设计文档
- **完成状态**: 🎉 100% 完成 (核心架构)
- **类型索引**: ✅ 重构完成 (150+个类型)
- **最后更新**: 2025-07-27 (CustomProperties类型安全优化)
- **原始文档**: Core_Types_Design.md (3116行) → 成功拆分成若干Phase_XXX.md。已删除，git中有旧档。
- **重构成果**:
  - ✅ **类型索引重构完成**: 建立完整的类型定义索引体系，实现"一次加载，全局认知"
  - ✅ **维护简化**: 移除重复的依赖关系维护，建立1对1维护关系
  - ✅ **认知效率**: LLM只需读取index即可建立完整的项目类型认知
- **重要决策**:
  - 基于Review建议优化配置一致性 (TitleContentFileName→BriefContentFileName)
  - 确定MVP Fast Fail异常处理策略，优化LLM代码理解和维护效率
  - Roslyn集成默认关闭，符合Phase 4实施计划
  - 实施约束层次验证机制：硬约束(SystemLimits/NodeConstraints) > 软约束(配置选项)
  - 澄清Token限制：单个CogNode(8K) vs 整个MemoTree视图(128K-200K)
  - **接口定义整合**: 将重复的存储接口定义整合到Phase2_StorageInterfaces.md，确保单一数据源原则
  - **配置选项归属优化**: 新增ViewOptions配置类，明确视图相关配置归属，优化文档间类型引用
  - **类型索引驱动**: 重构为类型索引驱动的架构认知模式，优化LLM使用体验
  - **🚀 内存优先架构 (v1.1)**: 采用常驻内存+同步落盘架构，移除复杂缓存层，简化MVP实现，充分利用现代硬件优势
  - **🆔 GUID编码优化 (v1.2)**: 识别当前12位截取方案的冲突风险，设计了三种LLM友好方案：Base64编码(22字符,已实施)、Base4096-CJK编码(11字符,开发中)、智能检索层(4-8字符灵活匹配,正式设计)，实现编码层与检索层的正交架构
  - **🎯 NodeId.Root优化 (v1.3)**: 解决Magic String问题，将根节点ID从硬编码"root"改为Guid.Empty的Base64编码"AAAAAAAAAAAAAAAAAAAAAA"，消除冲突风险，提升架构一致性，简化验证逻辑
  - **🔧 GuidEncoder接口化 (v1.4)**: 将ToBase64String重构为ToIdString抽象接口，消除方法名与具体算法的绑定，为未来切换Base4096-CJK等编码算法提供无缝支持，保持向后兼容
  - **🛡️ CustomProperties类型安全优化 (v1.5)**: 针对NodeMetadata.CustomProperties和NodeRelation.Properties的类型安全问题，提供MVP阶段的安全访问扩展方法(CustomPropertiesExtensions)，明确类型约定，规划Phase 5的JsonElement升级方案，在保持灵活性的同时显著提升类型安全性

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
