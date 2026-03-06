# 浮点数相等性语义设计备忘录

> **日期**：2026-03-06
> **涉及代码**：`ValueBox.Float.cs` 中的 `Update` 方法、`ITypeHelper<T>.Equals`、`ValueBox.ValueEquals`

## 背景

ValueBox 作为 StateJournal 对象图增量序列化的异构存储单元，提供了 RoundedDouble（有损 round-to-odd）和 ExactDouble（无损）两条 double 写入路径，以及 Single/Half 路径。各路径的 `Update` 方法需要判断"新旧值是否相同"来决定是否产生 diff。

问题集中在浮点数的两个特殊区域：**`-0.0` vs `+0.0`** 和 **NaN 的相等性**。

C# 的 `==` 运算符对它们的处理恰好和我们的需求**完全相反**：
- `==` 认为 `-0.0 == +0.0`（但它们的 IEEE 754 bits 不同，后续计算行为不同）
- `==` 认为 `NaN != NaN`（导致 Update 每次遇到 NaN 都不必要地重写）

## 三层相等性语义

| 层 | 名称 | `-0.0` vs `+0.0` | NaN 相等性 | 应用范围 |
|:---|:-----|:-----------------|:-----------|:---------|
| **Storage** | **BitExact** | 不等 | bit-equal 才等 | ExactDoubleFace.Update, DoubleHelper, ValueEquals |
| **Numeric** | **NumericEquiv** | **不等** | **所有 NaN 互等** | RoundedDouble / Single / Half 的 Update |
| C# `==` | *(参考)* | 等 | 全部不等 | *(不再使用)* |

### BitExact

纯粹按 IEEE 754 bit pattern 判断。用于：
- **ExactDoubleFace.Update**：用户选择 Exact 路径就是要 bit-level fidelity，`-0.0 ≠ +0.0`，不同 NaN payload 不等。
- **DoubleHelper / SingleHelper / HalfHelper**（`DictChangeTracker` 内部）：存储层的 dirty-tracking，只关心"字面上有没有变"。
- **ValueBox.ValueEquals**：值级别的通用比较，bit-level。

### NumericEquiv

"存储级别有没有发生可观测的数值变化"。应用于日常数值存储路径。核心考量：

**`-0.0 ≠ +0.0`**：`-0.0` 和 `+0.0` 的后续计算行为不同（`1.0 / -0.0 → -∞`，`Math.CopySign`，`Math.Atan2` 等）。如果用户的渐变计算从不同方向逼近零，得到的 `-0.0 / +0.0` 语义不同，丢弃这个差异会导致下游状态发散。

**所有 NaN 互等**：RoundedDouble 路径通过 round-to-odd 已丢弃了 NaN payload 的 LSB，payload 在此路径上本就不可靠。作为存储层，把所有 NaN 视为一样的不影响日常数值计算。用户需要 payload 区分（NaN-boxing / hash）时有 ExactDouble 兜底。

## 实现

### 新增内部工具函数

- **`TryDecodeDoubleRawBits(out ulong doubleBits)`**：`GetDouble` 的轻量兄弟函数，只检查 inline double 和 heap FloatingPoint 两条路径，不尝试整数转换。返回原始 IEEE 754 bits，同时覆盖了 inline 和 heap 两种存储形式。
- **`IsNaNBits(ulong doubleBits)`**：纯位运算判断 NaN，避免 sNaN→qNaN 平台差异。

### Update 方法改造

| Face | 旧实现 | 新实现 |
|:-----|:-------|:-------|
| **RoundedDoubleFace** | 仅比 inline `== value` | `TryDecodeDoubleRawBits` + bit-equal ∥ both-NaN |
| **ExactDoubleFace** | inline/heap 各用 `== value` | `TryDecodeDoubleRawBits` + bit-equal |
| **SingleFace** | `(float)decode == value` | `TryDecodeDoubleRawBits` + widened bit-equal ∥ both-NaN |
| **HalfFace** | `(Half)decode == value` | `TryDecodeDoubleRawBits` + widened bit-equal ∥ both-NaN |

## 未受影响的组件

- **`ITypeHelper<T>.Equals`**（`DoubleHelper` / `SingleHelper` / `HalfHelper`）：已经是 bit 比较，无需修改。
- **`ValueBox.ValueEquals`**：已经是 bit-level 比较，无需修改。
