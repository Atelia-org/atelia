---
docId: "docgraph-wish-instance-migration-plan"
title: "DocGraph v0.2：迁移方案（从 wishes/* 单文件到 wish/ 实例目录）"
produce_by:
  - "wish/W-0005-wish-instance-directory/wish.md"
status: "Draft"
version: "0.2"
created: "2026-01-05"
updated: "2026-01-05"
---

# DocGraph v0.2：迁移方案（从 wishes/* 单文件到 wish/ 实例目录）

## 目标

- 渐进迁移：允许 `wishes/` 与 `wish/` 并存。
- 保持可追溯性：旧路径仍可定位到新实例目录（通过 stub/redirect 或 index 记录）。
- DocGraph 对两套布局都能扫描与验证。

## 推荐迁移步骤（每个 Wish）

1. 创建实例目录：`wish/<wishId>-<slug>/`
2. 将旧 Wish 内容迁移到 `wish/<wishId>-<slug>/wish.md`
   - frontmatter 的 `wishId/title/status/.../produce` 保留
3. 在实例目录下创建骨架：
   - `project-status/{goals,issues,snapshot}.md`
   - `artifacts/{Resolve,Shape,Rule,Plan,Craft}/`
4. 将旧 Wish 文件替换为“redirect stub”（可选，但推荐）：
   - 保留原文件名与目录位置
   - 正文只包含一行：指向新 `wish.md` 的链接
   - frontmatter 中的 `produce` 更新为指向新位置（或仅保留 `SupersededBy` 信息）

## 样例（建议迁移对象）

- `W-0004`（已完成）适合作为第一个样例。