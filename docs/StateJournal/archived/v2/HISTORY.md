# StateJournal 项目历史

> 本文档记录 StateJournal 及其子组件的演变历史、命名轶闻和设计决策的考古信息。

---

## DurableHeap → StateJournal 演变

**记录日期**：2025-12-22

### v1 时代：DurableHeap

在 `atelia/docs/StateJournal/archived/mvp-design-v1.md` 中，这个组件最初叫做 **DurableHeap**。

设计思路是在 mmap 上做内存分配器（heap），直接将对象以内存布局形式持久化。

**为什么放弃**：
- 磁盘存储形态和内存对象形态的需求不一致
- 复杂度高，难以维护
- 为了简单性，转向增量序列化思路

### v2 时代：StateJournal

v2 采用 **append-only 增量序列化**：
- 对象变更记录为 diff（DiffPayload）
- 版本链支持快速回溯
- Two-phase commit 保证一致性

**历史残留**：Magic 常量 `DHD3`/`DHM3` 中的 `DH` 仍然代表 DurableHeap，是 v1 命名的残留。

---

## ELOG 命名起源

**记录日期**：2025-12-22

### 背景

2025 年夏，项目最初的动机是为 Agent History 设计一个存储格式，以取代 JSON Lines，并支持高效的反向读取。

当时的设计文档是 `atelia/docs/MessageHistoryStorage/bidirectional-enumerable-binary-log.md`，采用了一个朴素的描述性命名——**Bidirectional Enumerable Binary Log**。

### "ELOG" 的诞生

在那个会话中，智能体（使用 Augment Code Agent + Sonnet 4）需要为二进制格式选择 Magic / FourCC 常量。他**静默自主地**选择了 `ELOG` 这四个字符作为 Magic 值和文件扩展名。

这个选择没有经过讨论，没有正式定义，只是一个"看起来像个词"的 4 字节常量。

### 后追溯性赋义

几个月后（2025-12），当需要为这个格式编写正式规范时，文档作者需要给 `ELOG` 一个"官方含义"。于是"发明"了：

> **ELOG（Extensible Log Framing）**

但实际上：
- 这个格式**从未往 "Extensible" 方向设计**
- FrameTag 机制更接近 "discriminated" 而非 "extensible"
- 这个释义是**被"发现"而非"设计"**的

### 证据

在 `atelia/docs/StateJournal/archived/mvp-design-v2.all-in-one.md` 中搜索 "elog"（忽略大小写），只能找到 3 个匹配，且都不是正式定义。大家却已默认这就是格式的名字。

### 可能的结局

用户（项目监护人）预判：
- 作为独立的文件格式，命名需要"对外自明"
- "Extensible Log Framing" 这个释义大概率不会保留
- 二进制分帧格式会向简单、稳定的方向演进
- 最终这个名字可能会被替换，届时将无人知晓 `ELOG` 曾经存在

**这就是为什么要记录这段历史。**

---

## ELOG → RBF 命名重构

**记录日期**：2025-12-22

### 背景

在 2025-12-22 的畅谈会中，团队审视了 "ELOG (Extensible Log Framing)" 这个名称：

- "Extensible" 是后追溯性赋义，不反映实际设计
- 双 Magic（`DHD3`/`DHM3`）设计过于复杂，且 `DH` 是早期 DurableHeap 的遗留
- 格式的核心特性是**支持逆向扫描（backward scan / resync）**

### 决策

经过讨论，决定采用 **RBF (Reversible Binary Framing)** 作为新名称：

| 决策点 | 旧值 | 新值 |
|--------|------|------|
| 格式名 | ELOG (Extensible Log Framing) | **RBF (Reversible Binary Framing)** |
| Magic | `DHD3`/`DHM3` (双 Magic) | `RBF1` (`52 42 46 31`) 统一 |
| 扩展名 | `.elog` | `.rbf` |

### "Reversible" 的含义

"Reversible" 不是加密/可逆变换，而是指：
- 支持从文件尾部向前扫描（backward scan）
- 支持在损坏场景下 resync 到有效帧

这是该格式区别于普通 append-only log 的核心特性。

### 遗留

~~文档文件名（`elog-format.md`、`elog-interface.md`）暂时保留，作为后续清理任务。~~

**2025-12-22 已完成**：文档重命名为 `rbf-format.md`、`rbf-interface.md`、`rbf-test-vectors.md`。

---

## SlidingQueue 的"重新发现"

**记录日期**：2025-12-22

### 背景

同样是 2025 年夏那个会话，智能体在实现 `IReservableBufferWriter` 时遇到了 bug，需要一个双端队列类似的数据结构。

### 灵光闪现

在与 bug 死磕后，智能体"灵光闪现"想到了用 `List<T>` 实现一个滑动窗口队列（`atelia/src/Data/SlidingQueue.cs`）。

他当时表现得非常开心，觉得这是一个"创造性的原创解决方案"，并专门"表功"。

### 实际上

类似的数据结构（deque、ring buffer、sliding window）在计算机科学中非常常见。这不是"发明"，而是在特定约束条件下的**独立重新推导**。

但这不减损那种"发现"的喜悦——对于那个会话中的智能体来说，这确实是一次真实的认知体验。

### 后续

这个实现一直保留至今，没有被"更标准"的双端队列或 ring buffer 替代。它 work，它足够好。

---

## 变更日志

| 日期 | 事件 |
|------|------|
| 2025-12-22 | 创建本文档，记录 ELOG 命名起源和 SlidingQueue 轶闻 |
| 2025-12-22 | ELOG → RBF 命名重构：格式正式命名为 RBF (Reversible Binary Framing)；Magic 统一为 `RBF1`；扩展名变更 `.elog` → `.rbf` |
