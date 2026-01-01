---
docId: "W-0002-readme"
title: "DocGraph v0.1 README"
produce_by:
  - "wishes/active/wish-0002-doc-graph-tool.md"
---

# DocGraph v0.1 — 文档关系验证工具

> **版本**：v0.1.0  
> **状态**：已发布 ✅  
> **定位**：验证 Markdown 文档间 `produce`/`produce_by` 关系的 CLI 工具

## 快速开始

```bash
cd atelia/src/DocGraph

# 验证文档关系
dotnet run -- validate ../../../

# 有问题？预览修复方案
dotnet run -- fix ../../../ --dry-run

# 确认后执行修复
dotnet run -- fix ../../../ --yes
```

👉 **完整使用指南**：[USAGE.md](USAGE.md)

👉 **AI Team frontmatter 编写规范**：[maintain-frontmatter.md](../../../../agent-team/how-to/maintain-frontmatter.md)

## 核心功能

| 功能 | 状态 | 说明 |
|:-----|:-----|:-----|
| `validate` | ✅ | 验证 produce/produce_by 关系一致性 |
| `fix` | ✅ | 自动修复缺失的 frontmatter |
| `stats` | ✅ | 显示文档图统计信息 |
| `generate` | 🚧 | 汇总文档生成（计划中） |

## 设计文档

| 文档 | 用途 |
|:-----|:-----|
| [scope.md](scope.md) | 功能边界（做什么/不做什么） |
| [api.md](api.md) | 接口设计和数据模型 |
| [spec.md](spec.md) | 实现规范和验收标准 |

## 技术栈

- **.NET 9.0** + **System.CommandLine**
- **YamlDotNet** — YAML frontmatter 解析
- **xUnit** — 测试框架

## 参与贡献

所有设计决策基于 [scope.md](scope.md) 的功能边界。
通过畅谈会进行团队协作，采用"边商讨边实施"模式。

---

**问题反馈**：[wish-0002-doc-graph-tool.md](../../../../wishes/active/wish-0002-doc-graph-tool.md)
