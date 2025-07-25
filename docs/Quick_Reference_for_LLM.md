# LLM会话快速参考指南

> **目标**: 为后续LLM会话提供快速上手指南  
> **更新**: 每次会话结束时更新  

## 🎯 当前任务

**下一个要处理的文档**: `Phase3_RelationServices.md`
**当前进度**: 8/17 (47.1%)
**Phase1进度**: 4/4 (100%) ✅ **Phase 1 完成！**
**Phase2进度**: 3/3 (100%) ✅ **Phase 2 完成！**
**Phase3进度**: 1/4 (25.0%) 🚧 **Phase 3 进行中**
**预计完成时间**: 30分钟

## 📋 快速检查清单

### 会话开始时
- [ ] 阅读 `Document_Splitting_Plan.md`
- [ ] 检查 `splitting_progress.json` 确认当前任务
- [ ] 查看原文档 `Core_Types_Design.md` 的相关章节
- [ ] 确认依赖文档已存在

### 执行过程中
- [ ] 从原文档提取完整的代码块
- [ ] 保持所有注释和文档字符串
- [ ] 使用标准文档模板
- [ ] 添加适当的交叉引用

### 会话结束时
- [ ] 验证新文档的完整性
- [ ] 更新 `splitting_progress.json`
- [ ] 更新本快速参考指南
- [ ] 为下一个会话准备信息

## 🔍 当前状态快照

### 已完成文档
1. ✅ `Phase1_CoreTypes.md` - 核心数据类型 (300行)
2. ✅ `Phase1_Constraints.md` - 约束定义、验证规则 (220行)
3. ✅ `Phase1_Exceptions.md` - 异常类型定义、错误处理 (250行)
4. ✅ `Phase1_Configuration.md` - 配置选项、系统设置 (440行)
5. ✅ `Phase2_StorageInterfaces.md` - 存储接口定义 (395行)
6. ✅ `Phase2_RelationStorage.md` - 关系存储实现 (553行)
7. ✅ `Phase2_ViewStorage.md` - 视图状态存储、缓存策略 (300行)
8. ✅ `Phase3_CoreServices.md` - 核心业务服务接口 (479行)

### 🎉 Phase 1 & 2 完成！Phase 3 进行中
**Phase 1 基础类型阶段已全部完成**，**Phase 2 存储层阶段已全部完成**，**Phase 3 服务层阶段进行中** (1/4 完成)。

### 下一个任务详情
**文档**: `Phase3_RelationServices.md`
**内容**: 关系管理服务接口 (IRelationManagementService等)
**源位置**: 原文档第3.2节
**预计行数**: ~500行
**依赖**: Phase1_CoreTypes.md ✅, Phase2_RelationStorage.md ✅

## 📖 重要提醒

### 最新完成文档的特点
- **Phase3_CoreServices.md**: 完整的核心业务服务接口体系，包含认知画布服务、LOD生成服务、外部集成服务和环境信息服务，为系统提供高级业务功能抽象
- **Phase 1 总结**: 基础类型阶段已全部完成，包含核心类型、约束、异常和配置四大基础组件
- **Phase 2 总结**: 存储层阶段已全部完成，包含基础存储接口、关系存储实现和视图状态存储，为整个系统提供了完整的数据持久化抽象
- **Phase 3 进展**: 服务层阶段开始，已完成核心业务服务接口，下一步是关系管理服务

### 代码提取要点
1. **完整性**: 确保类型定义完整，包括所有属性和方法
2. **注释**: 保留所有XML文档注释和内联注释
3. **命名空间**: 保持一致的命名空间结构
4. **依赖**: 注意类型间的引用关系

### 文档结构要求
```markdown
# MemoTree [功能名称] (Phase X)
> 版本信息和元数据

## 概述
## [具体内容章节]
## 实施优先级
---
**下一阶段**: [链接]
```

### 常见陷阱
- ❌ 不要遗漏任何常量定义
- ❌ 不要破坏类型间的引用关系
- ❌ 不要忘记更新进度文件
- ❌ 不要省略文档头部信息
- ❌ 不要忘记为异常类型添加错误代码和上下文支持

## 🔗 关键文件位置

- **拆分计划**: `docs/Document_Splitting_Plan.md`
- **进度跟踪**: `docs/splitting_progress.json`  
- **原始文档**: `docs/Core_Types_Design.md`
- **已完成示例**: `docs/Phase1_CoreTypes.md`, `docs/Phase1_Constraints.md`, `docs/Phase1_Exceptions.md`, `docs/Phase1_Configuration.md`, `docs/Phase2_StorageInterfaces.md`, `docs/Phase2_RelationStorage.md`, `docs/Phase2_ViewStorage.md`, `docs/Phase3_CoreServices.md`
- **Partial Class示例**: `docs/PartialClass_Example.md`

## 📞 紧急情况处理

如果遇到以下情况：
1. **找不到源内容**: 检查原文档的章节编号是否正确
2. **依赖缺失**: 先完成依赖文档或创建占位符
3. **内容过多**: 考虑进一步拆分或移动到后续阶段
4. **类型冲突**: 检查是否有重复定义，调整文档边界

## 🎯 成功标准提醒

每个文档完成后应该：
- ✅ 功能内聚，职责清晰
- ✅ 代码完整，可独立理解
- ✅ 文档结构清晰易导航
- ✅ 交叉引用准确有效
- ✅ 行数控制在500-800行以内
- ✅ 包含实施优先级和最佳实践指南

---

**最后更新**: 2025-07-25 16:00
**下次更新**: 完成 Phase3_RelationServices.md 后
