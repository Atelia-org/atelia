# DocGraph 文档图谱工具

> 文档元信息聚合器 - 分散撰写，自动汇总鸟瞰

## 版本导航

### 🚀 v0.1 - 简化版MVP（当前开发中）
**定位**：文档元信息聚合器，仅处理frontmatter，快速生成汇总视图

- [v0.1/README.md](v0.1/README.md) - 版本概述
- [v0.1/scope.md](v0.1/scope.md) - 功能边界文档（核心！）
- [v0.1/api.md](v0.1/api.md) - 简化版API设计（待创建）
- [v0.1/spec.md](v0.1/spec.md) - 简化版规范（待创建）

### 🔮 future - 完整愿景（远期规划）
**定位**：完整的文档关系分析工具，包含链接追踪、双向链接检查等高级功能

- [future/README.md](future/README.md) - 愿景概述
- [future/api.md](future/api.md) - 完整API设计
- [future/spec.md](future/spec.md) - 完整规范
- [future/roadmap.md](future/roadmap.md) - 演进路线图

## 设计理念

### 核心价值
- **分散撰写**：团队成员可独立撰写和修订文档
- **自动汇总**：工具自动提取关键信息生成鸟瞰视图
- **机器可读**：frontmatter作为文档的机器接口
- **渐进演进**：从简单到复杂，快速交付核心价值

### 版本演进策略
1. **v0.1**：聚焦frontmatter解析和汇总，快速交付可用工具
2. **v1.0+**：逐步添加正文分析、链接追踪等高级功能
3. **future**：完整的文档生态系统工具

## 快速开始

### 使用v0.1（推荐）
```bash
# 安装后使用
docmeta scan ./wishes --output ./wishes/index.gen.md
```

### 查看设计文档
1. 先阅读 [v0.1/scope.md](v0.1/scope.md) 了解功能边界
2. 查看 [v0.1/api.md](v0.1/api.md) 了解接口设计
3. 查看 [v0.1/spec.md](v0.1/spec.md) 了解实现约束

## 贡献指南

### 文档贡献
- v0.1相关讨论请基于`scope.md`的功能边界
- future版本建议请提交到future目录
- 所有设计决策需记录在相应文档中

### 代码贡献
- v0.1实现基于简化版规范
- 保持向后兼容性
- 遵循规范驱动开发原则

---

**最后更新**：2025-12-31  
**当前焦点**：v0.1 MVP开发