# StateJournal Backlog

> 最后更新：2025-12-22
> 维护者：AI Team
> 
> **项目位置**：`atelia/docs/StateJournal/`
> **命名决议**：[2025-12-21 命名投票](../../../agent-team/meeting/2025-12-21-hideout-durableheap-naming-and-repo.md)

---

## ✅ 已完成任务

### 2025-12-22 迁移完成

| 任务 | 状态 | 说明 |
|------|------|------|
| 项目更名 | ✅ | DurableHeap → StateJournal |
| 文档迁移 | ✅ | 迁入 `atelia/docs/StateJournal/` |
| 名称替换 | ✅ | 全部文档批量替换完成 |

### 2025-12-21 MVP v2 审阅修订

| 任务 | 状态 | 说明 |
|------|------|------|
| P0-2：条款 ID 升级为稳定语义锚点 | ✅ | 43 个条款全部重命名 |
| P0-1：State 枚举升级为核心 API | ✅ | 新增 4 条条款 |
| P0-3："必须写死"升级为条款 | ✅ | 新增 `[F-RECORD-WRITE-SEQUENCE]` |
| P0-4：删除泛型写法矛盾 | ✅ | §3.4.5 |
| P1-5：Error Affordance 规范化 | ✅ | 新增 4 条条款 + ErrorCode 枚举 |
| P1-7：命名一致性 | ✅ | WriteDiff → WritePendingDiff |
| P1-8：DiscardChanges ObjectId 语义 | ✅ | 新增锚点条款 |
| P1-9：删除 Modified Object Set | ✅ | 术语表 |

---

## 📋 待办任务

### P1 优先级

| # | 任务 | 复杂度 | 说明 |
|---|------|--------|------|
| 1 | **SSOT + 内联摘要模式** | 高 | 需要全文审阅，识别重复定义 |

### P2 优先级

| # | 任务 | 复杂度 | 说明 |
|---|------|--------|------|
| 2 | LoadObject TryLoad 语义标注 | 低 | 明确返回 null 契约 |
| 3 | TryLoadObject 设计思路 | 中 | 待深入讨论 |

---

## 下一步：代码实现

| 阶段 | 内容 | 状态 |
|------|------|------|
| 设计 | MVP v2 规范 | ✅ 完成 |
| 实现 | `Atelia.StateJournal` 核心类库 | 🔜 待开始 |
| 测试 | 按 test-vectors 编写测试 | 🔜 待开始 |

---

## 相关文件

| 文件 | 说明 |
|------|------|
| [mvp-design-v2.md](mvp-design-v2.md) | 核心设计文档 |
| [mvp-test-vectors.md](mvp-test-vectors.md) | 测试向量 |
| [decisions/mvp-v2-decisions.md](decisions/mvp-v2-decisions.md) | 决策记录 |
