# L1 审阅任务包：Core 模块校准审阅

> **briefId**: L1-Core-2025-12-26-001
> **reviewType**: L1
> **createdBy**: Team Leader
> **createdAt**: 2025-12-26

---

## 🎯 焦点

**模块**：`atelia/src/StateJournal/Core/`

**specRef**:
- commit: HEAD (main branch)
- files:
  - `atelia/docs/StateJournal/mvp-design-v2.md` — §3.2.0.1 (VarInt), 术语表 (Ptr64, FrameTag)
  - `atelia/docs/StateJournal/rbf-interface.md` — §2.3 (<deleted-place-holder>)

---

## 📋 条款清单

### Group 1: VarInt 编解码

| ID | 标题 | 规范位置 | 要点 |
|:---|:-----|:---------|:-----|
| `[F-VARINT-CANONICAL-ENCODING]` | canonical 最短编码 | mvp-design-v2.md §3.2.0.1 | varuint/varint MUST 产生 canonical 最短编码 |
| `[F-DECODE-ERROR-FAILFAST]` | 解码错误 fail-fast | mvp-design-v2.md §3.2.0.1 | 遇到 EOF、溢出、非 canonical MUST 失败 |

**规范原文摘要**：

> `varuint`：无符号 base-128，每个字节低 7 bit 为数据，高 1 bit 为 continuation（1 表示后续还有字节）。`uint64` 最多 10 字节。
>
> `varint`：有符号整数采用 ZigZag 映射后按 `varuint` 编码。
> - ZigZag64：`zz = (n << 1) ^ (n >> 63)`
>
> **[F-VARINT-CANONICAL-ENCODING]** canonical 最短编码
> **[F-DECODE-ERROR-FAILFAST]** 解码错误策略：遇到 EOF、溢出、或非 canonical 一律视为格式错误并失败。

### Group 2: Ptr64 / <deleted-place-holder>

| ID | 标题 | 规范位置 | 要点 |
|:---|:-----|:---------|:-----|
| `[F-ADDRESS64-DEFINITION]` | <deleted-place-holder> 定义 | rbf-interface.md §2.3 | 8 字节 LE 文件偏移量 |
| `[F-ADDRESS64-ALIGNMENT]` | 4 字节对齐 | rbf-interface.md §2.3 | 有效地址 MUST `Value % 4 == 0` |
| `[F-ADDRESS64-NULL]` | Null 值定义 | rbf-interface.md §2.3 | `Value == 0` 表示 null |

**规范原文摘要**：

> **<deleted-place-holder>** 是 8 字节 LE 编码的文件偏移量，指向一个 Frame 的起始位置。
>
> - **[F-ADDRESS64-ALIGNMENT]**：有效 <deleted-place-holder> MUST 4 字节对齐（`Value % 4 == 0`）
> - **[F-ADDRESS64-NULL]**：`Value == 0` 表示 null（无效地址）

### Group 3: StateJournalError 类型

| ID | 标题 | 规范位置 | 要点 |
|:---|:-----|:---------|:-----|
| `[F-DECODE-ERROR-FAILFAST]` | VarInt 解码错误类型 | mvp-design-v2.md §3.2.0.1 | 需要对应的错误类型 |
| `[F-UNKNOWN-FRAMETAG-REJECT]` | 未知 FrameTag | mvp-design-v2.md 枚举值速查表 | Reader MUST fail-fast |
| `[F-UNKNOWN-OBJECTKIND-REJECT]` | 未知 ObjectKind | mvp-design-v2.md 枚举值速查表 | Reader MUST fail-fast |
| `[S-TRANSIENT-DISCARD-DETACH]` | 对象分离错误 | mvp-design-v2.md §3.1.0.1 | Detached 对象语义访问 MUST throw |

### Group 4: FrameTag 位段编码

| ID | 标题 | 规范位置 | 要点 |
|:---|:-----|:---------|:-----|
| `[F-FRAMETAG-STATEJOURNAL-BITLAYOUT]` | 位段布局 | mvp-design-v2.md 枚举值速查表 | 低 16 位 RecordType，高 16 位 SubType |
| `[F-FRAMETAG-SUBTYPE-ZERO-WHEN-NOT-OBJVER]` | 非 ObjVer 时 SubType=0 | mvp-design-v2.md | Reader MUST reject |
| `[F-OBJVER-OBJECTKIND-FROM-TAG]` | ObjectKind 来自 Tag | mvp-design-v2.md | 高 16 位解释为 ObjectKind |

**规范原文摘要**：

> | 位范围 | 字段名 | 类型 | 语义 |
> |--------|--------|------|------|
> | 31..16 | SubType | u16 | 当 RecordType=ObjectVersion 时解释为 ObjectKind |
> | 15..0 | RecordType | u16 | Record 顶层类型 |
>
> **计算公式**：`FrameTag = (SubType << 16) | RecordType`

### Group 5: IDurableObject 接口

| ID | 标题 | 规范位置 | 要点 |
|:---|:-----|:---------|:-----|
| `[A-OBJECT-STATE-PROPERTY]` | State 属性 | mvp-design-v2.md §3.1.0.1 | MUST 暴露，O(1)，MUST NOT throw |
| `[A-OBJECT-STATE-CLOSED-SET]` | 状态枚举封闭集 | mvp-design-v2.md §3.1.0.1 | 仅 4 个值 |
| `[A-HASCHANGES-O1-COMPLEXITY]` | HasChanges O(1) | mvp-design-v2.md §3.1.0.1 | 复杂度 MUST O(1) |

### Group 6: DurableObjectState 枚举

| ID | 标题 | 规范位置 | 要点 |
|:---|:-----|:---------|:-----|
| `[A-OBJECT-STATE-CLOSED-SET]` | 封闭集 | mvp-design-v2.md §3.1.0.1 | Clean, PersistentDirty, TransientDirty, Detached |

---

## 🔍 代码入口

| 文件 | 职责 | 条款关联 |
|:-----|:-----|:---------|
| `Core/VarInt.cs` | VarInt 编解码 | F-VARINT-*, F-DECODE-ERROR-FAILFAST |
| `Core/Ptr64.cs` | Ptr64 类型别名 | F-ADDRESS64-* |
| `Core/<deleted-place-holder>Extensions.cs` | <deleted-place-holder> 扩展方法 | F-ADDRESS64-ALIGNMENT |
| `Core/StateJournalError.cs` | 错误类型定义 | F-DECODE-ERROR-FAILFAST, F-UNKNOWN-* |
| `Core/StateJournalFrameTag.cs` | FrameTag 位段解释 | F-FRAMETAG-* |
| `Core/IDurableObject.cs` | 持久化对象接口 | A-OBJECT-STATE-*, A-HASCHANGES-* |
| `Core/DurableObjectState.cs` | 状态枚举 | A-OBJECT-STATE-CLOSED-SET |

**相关测试**：
- `Core/VarIntTests.cs`
- `Core/<deleted-place-holder>Tests.cs`
- `Core/StateJournalErrorTests.cs`
- `Core/StateJournalFrameTagTests.cs`
- `Core/IDurableObjectTests.cs`
- `Core/DurableObjectStateTests.cs`

---

## 📚 依赖上下文

**前置条款**：无（Core 是基础模块）

**术语定义**：参见 mvp-design-v2.md §术语表

---

## 📋 审阅指令

**角色**：L1 符合性法官

### MUST DO

1. 逐条款检查代码是否满足规范语义
2. 每个 Finding 必须引用：条款原文 + 代码位置 + 复现方式
3. 遇到规范未覆盖的行为 → 标记为 `U`（Underspecified），不是 `V`
4. 多个实现点都要检查并枚举

### MUST NOT

1. 不评论代码风格（那是 L3）
2. 不假设规范未写的约束
3. 不产出无法复现的 Finding
4. 不把 `U` 当作 bug 处理

### 特别关注

- **VarInt canonical 编码**：检查 `WriteVarUInt` 是否保证最短编码
- **VarInt 解码 fail-fast**：检查 `TryReadVarUInt` 对 EOF、溢出、非 canonical 的处理
- **FrameTag 位段计算**：检查 `GetRecordType`、`GetObjectKind` 的位运算
- **FrameTag 验证**：检查 `TryParse` 是否覆盖所有错误情况

---

## 📤 输出格式

**文件**：`atelia/docs/StateJournal/review/L1-Core-2025-12-26-findings.md`

**格式**：EVA-v1（参见 Recipe）

每个 Finding 使用：

```markdown
---
id: "F-{ClauseId}-{hash}"
verdictType: "V" | "U" | "C"
severity: "Critical" | "Major" | "Minor"  # 仅 V 类
clauseId: "[条款ID]"
dedupeKey: "{clauseId}|{normalizedLoc}|{verdictType}|{sig}"
---

# 🔴/🟡/🟢 {VerdictType}: [{ClauseId}] 简短描述

## 📝 Evidence

**规范**:
> "条款原文引用" (specFile §section)

**代码**: [`file:line`](相对路径#L行号)

**复现**:
- 类型: existingTest | newTest | manual
- 参考: ...

## ⚖️ Verdict

**判定**: {V/U/C} ({Severity}) — 问题描述

## 🛠️ Action

建议修复/澄清方案
```

---

## ✅ 审阅范围确认

- [x] 条款清单完整（6 组，~15 条）
- [x] 代码入口明确（7 个文件）
- [x] 依赖上下文：无（Core 是最底层）
- [x] 输出格式：EVA-v1
