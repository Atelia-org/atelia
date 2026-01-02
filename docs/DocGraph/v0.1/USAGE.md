---
docId: "W-0002-usage"
title: "DocGraph v0.1 使用指南"
produce_by:
  - "wishes/active/wish-0002-doc-graph-tool.md"
issues:
  - description: "需要更多 Visitor 实现（如依赖图生成器）"
    status: "open"
---

# DocGraph v0.1 — 使用指南

> 验证文档间的 `produce`/`produce_by` 关系一致性，并生成汇总文档。

---

## 快速开始

```bash
# 一键执行全流程：验证 + 修复 + 生成
docgraph

# 预览模式（不实际执行）
docgraph --dry-run

# 显示详细输出
docgraph --verbose
```

## 我需要它吗？

| ✅ 适合 | ⚠️ 暂不支持 |
|:--------|:------------|
| 维护 Wish → 产物文档的关系链 | Markdown 正文链接分析 |
| CI 集成验证 frontmatter 一致性 | 自动生成 `produce_by` 字段 |
| 检测悬空引用和缺失的反向链接 | 复杂的依赖图可视化 |
| 生成术语表和问题汇总文档 | |

📌 **AI Team**：若需撰写/维护 frontmatter 字段，请参阅 [maintain-frontmatter.md](../../../../agent-team/how-to/maintain-frontmatter.md)

---

## 命令速查

### 默认命令 — 全流程执行

```bash
docgraph [path]           # 执行全流程：validate + fix + generate
docgraph --dry-run        # 预览模式，不实际执行
docgraph --yes            # 跳过确认，自动执行（CI 场景）
docgraph --force          # 即使有 Error 也继续生成（不推荐）
docgraph --verbose        # 显示详细输出
```

**全流程输出示例**：
```
═══════════════════════════════════════════════════════════
                    DocGraph 全流程执行
═══════════════════════════════════════════════════════════

📂 阶段 1/3：扫描文档图
   ✅ 发现 2 个 Wish 文档，6 个产物文档

🔍 阶段 2/3：验证并修复
   ✅ 验证通过，无问题

📝 阶段 3/3：生成汇总文档
   ✅ 已生成: docs/glossary.gen.md
   ✅ 已生成: docs/issues.gen.md

═══════════════════════════════════════════════════════════
                        ✅ 全流程完成
═══════════════════════════════════════════════════════════
```

### `validate` — 仅验证（不修复不生成）

```bash
docgraph validate [path]  # path 默认为当前目录
```

**检查内容**：
- Wish 文档的 `produce` 字段指向的文件是否存在
- 产物文档的 `produce_by` 字段是否正确指回 Wish
- 必填字段（`title`、`produce`/`produce_by`）是否缺失

**输出示例**：
```
✅ 验证通过：3 个 Wish 文档，7 个产物文档，10 条关系
```

或：
```
❌ 发现 2 个问题：

🔴 [MUST FIX] DOCGRAPH_RELATION_DANGLING_LINK
   位置：wishes/active/wish-0002.md
   问题：produce 目标不存在：atelia/docs/DocGraph/missing.md
   建议：检查路径是否正确，或移除无效的 produce 引用

🟡 [SHOULD FIX] DOCGRAPH_RELATION_MISSING_BACKLINK
   位置：atelia/docs/DocGraph/api.md
   问题：缺少 produce_by 字段
   建议：添加 produce_by: ["wishes/active/wish-0002.md"]
```

### `fix` — 自动修复问题

```bash
docgraph fix [path]          # 交互模式，逐个确认
docgraph fix [path] --dry-run  # 只预览，不执行
docgraph fix [path] --yes      # 自动确认（CI 场景）
```

**可自动修复**：
- 创建缺失的产物文档（添加最小 frontmatter）
- 注入缺失的 `produce_by` 字段

**不可自动修复**（需人工处理）：
- 悬空引用（produce 指向的文件不存在）
- YAML 语法错误

### `stats` — 统计信息

```bash
docgraph stats [path]
```

**输出示例**：
```
📊 文档图统计

Root Nodes (Wish 文档)：3
产物文档：7
总关系数：10
图深度：2

按状态分布：
  active：2 个 Wish
  completed：1 个 Wish
```

---

## 退出码

| 退出码 | 含义 | 场景 |
|:-------|:-----|:-----|
| `0` | 成功 | 验证通过，无错误无警告 |
| `1` | 警告 | 有警告但无错误 |
| `2` | 错误 | 有验证错误需修复 |
| `3` | 致命 | 无法执行（配置错误、IO 错误） |

**CI 集成示例**（GitHub Actions）：

```yaml
- name: 验证文档关系
  run: |
    cd atelia/src/DocGraph
    dotnet run -- validate ../../../
    # 退出码非 0 时 CI 失败
```

---

## 核心概念

### 文档图（Document Graph）

DocGraph 构建的不是文件系统树，而是 **frontmatter 关系构成的有向图**：

```
wishes/active/wish-0002.md  ──produce──>  atelia/docs/DocGraph/api.md
                            ──produce──>  atelia/docs/DocGraph/spec.md
                                               │
                                               └──produce_by──> wishes/active/wish-0002.md
```

### Root Nodes

**Wish 文档**是图的入口点，位于 `wishes/active/`、`wishes/biding/` 和 `wishes/completed/` 目录。
DocGraph 从这些 Root Nodes 开始，沿着 `produce` 关系递归构建完整的文档闭包。

### 双向验证

- **`produce`**：Wish 文档声明"我产生了这些产物文档"
- **`produce_by`**：产物文档声明"我被哪个 Wish 产生"

DocGraph 验证这两个方向的声明是否一致。你只需维护 `produce`，工具会检查 `produce_by` 是否匹配。

---

## 常见问题

### Q: `generate` 命令在哪里？

`generate` 功能（生成 `glossary.gen.md`、`issues.gen.md` 等汇总文档）**尚未实现**。

当前状态：Visitor 模式架构已就绪（见 `src/DocGraph/Visitors/`），但 CLI 命令未添加。

跟踪进度：[wish-0002-doc-graph-tool.md](../../../../wishes/active/wish-0002-doc-graph-tool.md)

### Q: 我的文档没有被扫描到？

检查以下条件：
1. 文件是否在 `wishes/active/`、`wishes/biding/` 或 `wishes/completed/` 目录？
2. 文件是否有 YAML frontmatter（`---` 开头和结尾）？
3. 是否被某个 Wish 的 `produce` 字段引用？

**未被引用的文档不在图中**——这是设计决策，不是 bug。

### Q: `produce_by` 必须手动维护吗？

是的，当前版本需要手动维护。`fix` 命令可以在产物文档缺失 frontmatter 时创建模板，但不会修改已有的 frontmatter。

未来版本可能支持自动注入 `produce_by`。

### Q: 如何处理 YAML 解析错误？

DocGraph 会跳过 YAML 解析失败的文件并报告错误位置。检查错误提示中的行号和列号，修复 YAML 语法问题。

常见问题：
- 缩进不一致（混用 Tab 和空格）
- 冒号后缺少空格（`title:value` → `title: value`）
- 数组格式错误（`produce: path.md` → `produce: ["path.md"]`）

---

## 下一步

- 阅读 [scope.md](scope.md) 了解功能边界
- 阅读 [api.md](api.md) 了解数据模型和错误码详情
- AI Team：阅读 [maintain-frontmatter.md](../../../../agent-team/how-to/maintain-frontmatter.md) 了解 frontmatter 编写规范

---

**版本**：v0.1.0 | **更新日期**：2026-01-01
