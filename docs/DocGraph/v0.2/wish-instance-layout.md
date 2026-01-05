---
docId: "docgraph-wish-instance-layout"
title: "DocGraph v0.2：Wish 实例布局（扫描与约定）"
produce_by:
  - "wish/W-0005-wish-instance-directory/wish.md"
status: "Draft"
version: "0.2"
created: "2026-01-05"
updated: "2026-01-05"
---

# DocGraph v0.2：Wish 实例布局（扫描与约定）

> 本文档描述 DocGraph 在“每个 Wish 一个实例目录”布局下的扫描约定。

## 扫描入口（Root Nodes）

- Root Nodes =
  - 旧布局：`wishes/{active,biding,completed}/**/*.md` 中带 frontmatter 的文件（保持 v0.1 行为）
  - 新布局：`wish/**/wish.md`（仅此文件视为 Wish Root）

## WishId / Status 推导

- 对新布局的 `wish/**/wish.md`：
  - docId 使用 `frontmatter.wishId`
  - status 使用 `frontmatter.status` 并归一化为小写（`Active`→`active`）

## Artifact 文件发现策略

- 新布局下，`wish/**` 中除 `wish.md` 外的其他 `.md` 文件不作为 Root Nodes 扫描。
- 它们应由 `produce` 闭包追踪机制发现（保持“图驱动、非树驱动”的核心设计）。