# StateJournal 文档重构执行计划

> **版本**：v1.1
> **状态**：Approved（已批准）
> **Owner**：DocOps
> **批准日期**：2025-12-29
> **审阅**：Advisor-GPT (GPT-5.2) — Approve with conditions → v1.1 已修复
> **来源畅谈会**：[2025-12-29-doc-restructuring-methodology.md](../../agent-team/meeting/StateJournal/2025-12-29-doc-restructuring-methodology.md)

---

## 0. 全局不变式（Phase 1 期间必须遵守）

以下规则在整个 Phase 1 期间**不可违反**：

### INV-1: Anchor Stability（锚点稳定性）

**不允许修改任何 `[S-*]` / `[A-*]` 条款锚点字符串。**

- ✅ 允许：移动条款所在段落/文件
- ❌ 禁止：重命名条款 ID（如 `[S-OLD-NAME]` → `[S-NEW-NAME]`）
- 理由：锚点字符串是全局引用的 SSOT，修改会导致系统性断裂

### INV-2: Redirect Stub（迁移期重定向）

**对任何被迁移的"定义段落"，旧位置必须保留最小 stub。**

Stub 格式：
```markdown
> **已迁移**：本节内容已迁移至 [新文件路径](链接)。
> 相关条款：`[S-XXX]`, `[A-YYY]`
```

- 适用范围：术语表、条款定义段落、任何被搬迁的章节
- 保留时长：直到全局搜索确认无旧格式引用（或 Phase 2 完成）

### INV-3: Primary Definition 唯一性

**每个条款/术语在整个文档群中只能有一个 Primary Definition。**

- 其他出现必须是"引用"而非"定义"
- 引用处不得出现定义句式（如 "X is ..." 或 "X 定义为 ..."）

---

## 1. 背景与目标

### 1.1 问题陈述

`mvp-design-v2.md` 已膨胀至约 1,200 行，超出 LLM 的精确理解窗口，导致：
- 实现与设计偏离（例如 DurableDict 三数据结构 vs 双字典策略）
- 审阅时难以定位具体条款
- 新增内容不知道放在哪里

### 1.2 成功的定义

**Phase 1 完成标准**：
1. ✅ 存在条款注册表，51 个条款有唯一 Primary Definition
2. ✅ 术语表独立为 `glossary.md`，核心 10 术语可独立解释
3. ✅ 存在首个场景卡片 `scenarios/loadobject.md`，引用条款可追溯

**长期目标**（Phase 2+）：
- 将 `mvp-design-v2.md` 拆分为 6 个主题文档（< 300 行/个）
- 建立完整的场景卡片导航系统

### 1.3 设计共识（来自畅谈会）

| 来源 | 贡献 |
|:-----|:-----|
| **Advisor-Claude** | 六维度三层架构；"编译单元"模式 |
| **Advisor-DeepSeek** | "场景卡片 + 文档包"模式；DX 优先 |
| **Advisor-GPT** | 条款注册表；51 条款统计；验收标准 |

---

## 2. Phase 1 任务清单

### Task 1.0: 建立条款注册表

**Owner**: DocOps  
**预估工时**: 2h  
**依赖**: 无（首个任务）  
**工作目录**: `/repos/focus/atelia/docs/StateJournal/`

#### 输入
- `mvp-design-v2.md`（当前目录下）

#### 输出
- `clauses/index.md` — 条款注册表

#### 执行步骤

1. **提取所有条款锚点**
   ```bash
   # 工作目录：/repos/focus/atelia/docs/StateJournal/
   cd /repos/focus/atelia/docs/StateJournal/
   
   # 提取并统计（使用 grep -E，兼容 GNU grep）
   grep -oE '\[S-[A-Z0-9-]+\]|\[A-[A-Z0-9-]+\]' mvp-design-v2.md | sort | uniq -c | sort -rn
   
   # 期望输出样例（前 5 行）：
   #   4 [S-OBJECTID-RESERVED-RANGE]
   #   3 [A-LOADOBJECT-RETURN-RESULT]
   #   2 [S-NEW-OBJECT-AUTO-DIRTY]
   #   2 [S-TRANSIENT-DISCARD-DETACH]
   #   ...
   ```
   
   **故障排查**：
   - 若输出为空：检查路径是否正确、文件是否存在
   - 若 grep 报错 `-E`：改用 `grep -o -E` 或确认 GNU grep 版本
   - 若中文乱码：确认文件编码为 UTF-8

2. **去重并分类**
   ```bash
   # 仅获取唯一条款列表
   grep -oE '\[S-[A-Z0-9-]+\]|\[A-[A-Z0-9-]+\]' mvp-design-v2.md | sort -u > /tmp/clauses.txt
   wc -l /tmp/clauses.txt
   # 期望：约 51 行
   ```
   
3. **确定 Primary Definition**
   
   **形式化判定规则**（可复查）：
   
   | 优先级 | 判定条件 | 示例 |
   |:-------|:---------|:-----|
   | **P1** | 条款锚点位于 `**[S-XXX]**` 粗体格式中 | `**[S-OBJECTID-RESERVED-RANGE]**` |
   | **P2** | 条款锚点位于 `### 标题` 或 `#### 标题` 行中 | `#### [A-LOADOBJECT-RETURN-RESULT] LoadObject 返回形态` |
   | **P3** | 条款锚点所在段落以定义句式开头（"X MUST ..."、"X 定义为 ..."） | `[S-XXX] Allocator MUST NOT ...` |
   
   - 按 P1 > P2 > P3 优先级选择 Primary Definition
   - 若某条款多处满足同一优先级，选择**文档中首次出现**的位置
   - 在注册表 Notes 列记录"定义段落首句摘要（≤20 字）"，便于复核

4. **生成注册表**
   
   创建 `clauses/index.md`：
   ```markdown
   # StateJournal 条款注册表
   
   > **版本**：v1.0
   > **生成日期**：YYYY-MM-DD
   > **条款总数**：N（S: M, A: K）
   
   ## 条款索引
   
   | ClauseId | Type | PrimaryDefinitionDoc | Line | FirstSentence |
   |:---------|:-----|:---------------------|:-----|:--------------|
   | [S-OBJECTID-RESERVED-RANGE] | S | mvp-design-v2.md | 123 | Allocator MUST NOT 分配... |
   | [A-LOADOBJECT-RETURN-RESULT] | A | mvp-design-v2.md | 456 | LoadObject MUST 返回... |
   | ... | ... | ... | ... | ... |
   ```

5. **验证并处理差异**
   
   若提取结果 ≠ 51：
   - 在注册表 Notes 中解释差异来源（新增条款/旧条款被删除/统计基线变化）
   - **必须同步主持人确认后方可进入 Task 1.1**

#### 验收标准

| 检查项 | 标准 |
|:-------|:-----|
| **完整性** | 注册表包含提取结果的**全部唯一条款**；基线 51（33 S + 18 A），若≠51 需解释并获确认 |
| **唯一性** | 每个条款恰有 **1 个** Primary Definition |
| **可定位** | Primary Definition 指向的文件路径存在，且该行确实包含该条款 |
| **可复查** | 每条记录包含 FirstSentence 摘要，便于人工验证 |
| **无冲突** | 任何条款不得出现多个 Primary Definition |

---

### Task 1.1: 提取术语表为 Layer 0

**Owner**: DocOps  
**预估工时**: 3h  
**依赖**: Task 1.0（需要条款注册表确认术语相关条款）  
**工作目录**: `/repos/focus/atelia/docs/StateJournal/`

#### 输入
- `mvp-design-v2.md` 的 §术语表 章节
- Task 1.0 产出的 `clauses/index.md`

#### 输出
- `glossary.md` — 独立术语表
- `mvp-design-v2.md` 原位置的 stub（遵守 **INV-2**）

#### 执行步骤

1. **识别术语表边界**
   - 定位 `mvp-design-v2.md` 中 `## 术语表（Glossary）` 的起止行
   - 预期：约 150-200 行

2. **拆分内容**
   - **保留在 glossary.md**：概念定义、枚举值表（FrameTag/ObjectKind/ValueType）、命名约定
   - **迁移到主题文档或标注为非规范性**：实现映射（`_committed`/`_current` 等代码标识符）

3. **创建 glossary.md**
   - 添加文档头部（版本、状态、依赖声明）
   - 为每个术语建立 Markdown 锚点（如 `### ObjectId {#objectid}`）
   - 核心 10 术语优先处理：
     1. ObjectId
     2. Ptr64 / <deleted-place-holder>
     3. ObjectVersionPtr
     4. VersionIndex
     5. EpochSeq
     6. Dirty Set
     7. Identity Map
     8. Commit / Commit Point
     9. HEAD
     10. Working State / Committed State

4. **更新原文件**
   - 在 `mvp-design-v2.md` 原术语表位置保留 stub：
     ```markdown
     ## 术语表（Glossary）
     
     > **已迁移**：术语表已独立为 [glossary.md](glossary.md)。
     > 本章节保留为兼容性指引，请查阅新文件获取最新定义。
     ```

5. **验证引用完整性**
   - 搜索 `mvp-design-v2.md` 中对术语的引用
   - 确保术语定义在 `glossary.md` 中存在

#### 验收标准

| 检查项 | 标准 |
|:-------|:-----|
| 独立可读 | `glossary.md` 能在**不加载其他文件**时解释核心 10 术语 |
| 无重复定义 | 每个术语只在 `glossary.md` 中有一个 Primary Definition |
| Stub 存在 | `mvp-design-v2.md` 原位置有指向 `glossary.md` 的 stub |
| 非规范性标注 | 实现映射（如 `_committed`）若保留在 glossary 中，必须标注为 `(Informative)` |

---

### Task 1.2: 创建首个场景卡片

**Owner**: DocOps  
**预估工时**: 2h  
**依赖**: Task 1.0 + Task 1.1（需要条款注册表和术语表）  
**工作目录**: `/repos/focus/atelia/docs/StateJournal/`

#### 输入
- Task 1.0 产出的 `clauses/index.md`
- Task 1.1 产出的 `glossary.md`
- `mvp-design-v2.md` 中 LoadObject 相关章节

#### 输出
- `scenarios/loadobject.md` — 首个场景卡片
- `scenarios/index.md` — 场景索引（仅包含 LoadObject）

#### 执行步骤

1. **创建 scenarios 目录结构**
   ```
   atelia/docs/StateJournal/scenarios/
   ├── index.md        # 场景索引
   └── loadobject.md   # LoadObject 场景卡片
   ```

2. **编写场景卡片**
   - 格式参考：
     ```markdown
     # 场景：实现 LoadObject(ObjectId)
     
     > **用户故事**："我是 Implementer，正在实现 StateJournal 的读路径。
     > 我需要知道如何从 ObjectId 找到对象的最新版本，并 materialize 成内存对象。"
     
     ## 📦 核心文档包（必须加载）
     
     | 文档 | 用途 | 行数 |
     |:-----|:-----|:-----|
     | [glossary.md](../glossary.md) | 术语定义 | ~200 |
     | mvp-design-v2.md §3.1.2 | LoadObject 语义 | ~50 |
     | mvp-design-v2.md §3.3.2 | LoadObject 实现 | ~30 |
     
     ## 📦 扩展文档包（按需加载）
     
     | 文档 | 何时需要 |
     |:-----|:---------|
     | mvp-design-v2.md §3.1.0.1 | 当需要理解 Identity Map / Dirty Set 时 |
     | mvp-design-v2.md §3.2.4 | 当需要理解 VersionIndex 查找时 |
     
     ## 🔑 关键条款
     
     | 条款 | 要点 |
     |:-----|:-----|
     | [A-LOADOBJECT-RETURN-RESULT] | 返回 `AteliaResult<T>` 而非 null |
     | [S-LAZYLOAD-DISPATCH-BY-OWNER] | Lazy Load 按 Owning Workspace 分派 |
     | [S-WORKSPACE-OWNING-EXACTLY-ONE] | 每个对象绑定唯一 Workspace |
     
     ## 🔗 相关场景
     
     - [实现 Commit()](commit.md)（待创建）
     - [实现 DurableDict](durabledict.md)（待创建）
     ```

3. **创建场景索引**
   ```markdown
   # StateJournal 实现场景索引
   
   > 本索引帮助 Implementer 快速定位"实现某功能需要加载哪些文档"。
   
   ## 读路径
   
   - [实现 LoadObject](loadobject.md) ✅
   - 实现 Open（待创建）
   
   ## 写路径
   
   - 实现 Commit（待创建）
   - 实现 DurableDict（待创建）
   
   ## 恢复路径
   
   - 实现崩溃恢复（待创建）
   ```

4. **验证条款可追溯**
   - 场景卡片中引用的所有条款，都能在注册表中定位到 Primary Definition

#### 验收标准

| 检查项 | 标准 |
|:-------|:-----|
| 导航有效 | 仅加载 `scenarios/loadobject.md` + `glossary.md` 能知道"还需要加载哪些文档" |
| 条款可追溯 | 引用的所有条款都在注册表中有 Primary Definition |
| 格式一致 | 场景卡片包含：用户故事、核心包、扩展包、关键条款、相关场景 |
| 索引存在 | `scenarios/index.md` 存在且链接到 `loadobject.md` |

---

## 3. 依赖图与执行顺序

```
Task 1.0: 条款注册表
    │
    ▼
Task 1.1: 术语表提取
    │
    ▼
Task 1.2: 场景卡片试点
```

**执行模式**：串行（每个 Task 依赖前一个的产出）

**建议的会话策略**：
- DocOps 可以在单个会话中完成 Task 1.0
- 完成后将进度和上下文写入 `agent-team/members/DocOps/inbox.md`
- 下一个会话继续 Task 1.1，以此类推

---

## 4. 后续 Phase（待定）

Phase 1 完成后，基于实践经验决定是否继续：

| Phase | 内容 | 前置条件 |
|:------|:-----|:---------|
| **Phase 2** | 按六维度拆分 mvp-design-v2.md 正文 | Phase 1 验收通过 |
| **Phase 3** | 完整场景卡片目录（5+ 场景） | Phase 2 完成 |
| **Phase 4** | 条款追踪工具（脚本/CI） | 有足够的手工痛点 |

---

## 5. 一致性风险检查清单

执行过程中需要持续关注（来自 Advisor-GPT 核查）：

| # | 风险 | 缓解措施 |
|:--|:-----|:---------|
| 1 | 条款定义 vs 引用混淆 | 严格执行 Primary Definition 规则（见 Task 1.0 判定表） |
| 2 | 术语表 SSOT 破裂 | 所有术语定义只在 glossary.md；遵守 **INV-3** |
| 3 | 跨文档前置条件丢失 | 场景卡片列出"扩展包" |
| 4 | 重复出现条款版本不一致 | 迁移时全量搜索并更新；遵守 **INV-2** |
| 5 | 引用链断裂（§x.y.z 失效） | 优先使用条款锚点而非章节号 |
| 6 | 条款锚点被意外修改 | 遵守 **INV-1**；PR Review 时检查锚点字符串不变 |
| 7 | 依赖图循环/隐式依赖膨胀 | Phase 2 开始前建立 `dependencies.md`；文档头部声明依赖 |

---

## 6. 产出物清单

Phase 1 完成后应存在以下文件：

```
atelia/docs/StateJournal/
├── clauses/
│   └── index.md              # Task 1.0 产出
├── glossary.md               # Task 1.1 产出
├── scenarios/
│   ├── index.md              # Task 1.2 产出
│   └── loadobject.md         # Task 1.2 产出
├── mvp-design-v2.md          # 更新（术语表 stub）
└── doc-restructuring-plan.md # 本文档
```

---

_Last updated: 2025-12-29 (v1.1 — incorporated Advisor-GPT review feedback)_
