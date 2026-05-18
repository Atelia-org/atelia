# TextAdv 黑盒游戏测试反馈

> 测试日期：2026-05-18
> 测试者：AI Agent（通过 `pmux game *` 命令交互）
> 游戏路径：沙滩 → 密林 → 浆果丛 → 泉水 → 泉底遗迹（符文石、符文阵、符文碎片、晶石、石匣）
> 回合数：约 20+ 回合（Day 1 早晨 → Day 2 夜晚）

## 一、总体体验

**一句话**：玩得很投入！荒岛求生的开局简洁有力，从沙滩向北探索密林、发现浆果、找到泉水、最终揭开泉底符文遗迹的探索链条自然流畅。Amnesia 主题与"必须外化记忆到 Notebook"的机制结合得很好，确实制造了"我需要认真记笔记否则会忘"的紧迫感。

---

## 二、技术层面

### 2.1 PipeMux 集成 — ✅ 运行良好

整个试玩过程中，`pmux game *` 命令响应迅速，状态跨命令正确保持。未遇到进程卡死、状态丢失或回显异常。PipeMux + StateJournal 的持久化架构经受住了连续 20+ 回合的考验。

### 2.2 回合结构（Small-Action / Large-Action）— ✅ 设计合理

- `edit-memory-notebook`（Small-Action）不结束回合、可多次执行的设定很自然
- `explore` / `interact` / `rest-a-while`（Large-Action）结束回合并推进时间的区分清晰
- `look-around` 随时可查、不消耗回合的设计很贴心

### 2.3 GM Agent 世界结算 — ✅ 表现出色

GM Agent 在以下场景中都给出了合理且有趣的世界推进：
- 探索未知方向时动态创建新地点（密林向北 → 创建了浆果丛和泉水）
- 交互 affordance 的结算（检查浆果 → 确认无毒并采集；触碰符文 → 触发符文阵浮现）
- 符文石、碎片、晶石、石匣的连锁谜题推进自然，没有出现逻辑断裂

### 2.4 世界编辑工具链 — ✅ 架构清晰

从代码层面看，`GmWorldEditService` 提供的 `CreateLocation` / `LinkLocations` / `CreateItem` 等工具通过 `MethodToolWrapper` 暴露给 GM Agent 的架构合理。账本分层（L0 World Ledger → L5 Narrative Rendering）的设计方向正确。

---

## 三、核心问题：Validator 看不见上回合结算结果 🔴

### 3.1 现象描述

这是整个试玩过程中 **最影响体验的问题**。具体表现为：

1. 玩家在上回合通过 `interact` 执行了某个动作（如"检查浆果"），GM 结算返回了明确结果（如"浆果确认无毒"）
2. 下一回合玩家在事前推理中引用该结果（如"上回合已确认浆果无毒，现在采集"），validator **拒绝通过**，理由是"输入中不存在浆果无毒的证据"
3. 玩家被迫反复修改措辞，从"浆果无毒"退到"浆果外观正常、无霉斑"，再到仅描述"浆果呈深紫色、果实饱满"，才能通过验证

### 3.2 根因分析

问题出在 `GameActionValidator.BuildObservation()` 方法（`prototypes/TextAdv/GameActionValidator.cs`）。

当前 `BuildObservation` 向 validator 模型发送的上下文包括：
- 当前可见信息（位置、出口、物品、角色、交互）
- Memory-Notebook 当前块视图
- 当前回合已接受步骤（`AcceptedSteps`）
- 候选动作的事前推理和动作参数

**缺失的关键信息**：
- `perception.LastResolution` — 上回合 GM 结算的完整文本 **没有被传入 validator prompt**

而 `PerceptionBundle` 中明明有这个字段：

```csharp
// GameDtos.cs
internal sealed record PerceptionBundle(
    // ... 其他字段 ...
    string? LastResolution  // ← 这个字段在 BuildObservation 中未被使用
);
```

`GamePresenter.RenderPerception()` 会把它渲染给玩家看：

```csharp
// GamePresenter.cs
if (!string.IsNullOrWhiteSpace(perception.LastResolution)) {
    sb.AppendLine("📣 上回合结算:");
    AppendIndented(sb, perception.LastResolution!);
}
```

但 `GameActionValidator.BuildObservation()` 完全没有提取 `perception.LastResolution`。这导致：

- **Validator 和玩家看到的信息不对称**：玩家看到了上回合结算文本，validator 看不到
- **玩家无法在事前推理中引用任何历史回合的结果**，因为 validator 的 evidence boundary 检查会拒绝所有"输入中不存在"的事实
- **Amnesia 主题被过度执行**：设计意图是"角色失忆所以要记笔记"，但 validator 的盲区导致"连刚刚发生的事情都无法引用"，变成了"系统失忆"而非"角色失忆"

### 3.3 修复建议

在 `GameActionValidator.BuildObservation()` 中增加对 `perception.LastResolution` 的注入。具体修改位置在 `GameActionValidator.cs` 的 `BuildObservation` 方法中，在 `[Memory-Notebook 当前块视图]` 之前或之后插入：

```csharp
// 在 sb.AppendLine("[Memory-Notebook 当前块视图]"); 之前加入：
if (!string.IsNullOrWhiteSpace(perception.LastResolution)) {
    sb.AppendLine();
    sb.AppendLine("[上回合结算结果]");
    sb.AppendLine(perception.LastResolution);
}
```

同时建议在 validator 的 System Prompt 中补充一条判定原则：

> 9. 上回合结算结果（[上回合结算结果]）中描述的事实，视为当前回合已确立的世界状态，玩家可以在事前推理中直接引用。但玩家不能把结算结果中未出现的内容说成已发生。

### 3.4 延伸思考：是否需要更完整的历史上下文？

当前 validator 只能看到"当前回合已接受步骤"，看不到任何跨回合历史。即使修复了 `LastResolution` 的注入，validator 也只能看到紧邻上一回合的结算。考虑以下场景：

- 玩家在 Day 1 采集了浆果，Day 2 想引用"背包里有浆果"——这可以通过 `InventoryItems` 验证（✅ 已覆盖）
- 玩家在 3 回合前发现了一个符文符号，现在想引用它的形状——`LastResolution` 不覆盖（⚠️ 需通过 Notebook 间接覆盖）

目前的 Amnesia 设计下，要求玩家把重要发现写进 Notebook 再引用是合理的。但 `LastResolution` 的注入是底线修复——至少不能出现"刚发生的事 validator 不认"的情况。

---

## 四、游戏设计层面

### 4.1 世界构建 — ✅ 开局简洁有效

沙滩 → 密林的两地点开局恰到好处。沙滩给出"向北"的明确方向，密林给出"遮天蔽日"的氛围和向南回沙滩的退路。没有信息过载，玩家自然产生"向北探索"的动机。

### 4.2 探索循环 — ✅ 有节奏感

- 发现浆果丛 → 检查 → 确认无毒 → 采集 → 继续探索水源
- 找到泉水 → 喝水 → 观察泉眼 → 发现符文 → 深入探索

这个"发现 → 观察 → 交互 → 深入"的循环很自然，符合探索类游戏的心流。

### 4.3 谜题设计 — ✅ 符文遗迹的连锁谜题出色

泉底符文遗迹的多阶段谜题设计得很好：

1. 仔细观察泉眼 → 发现泉底符文石
2. 查看符文纹路 → 发现眼睛图案和螺旋线
3. 触碰螺旋纹 → 触发符文阵浮现
4. 观察符文图案 → 发现三层同心圆地图
5. 触碰泉底符文 → 唤醒金属碎片
6. 将碎片嵌入螺旋纹凹槽 → 水面退去，露出石阶
7. 捧泉水 → 发现发光晶石
8. 晶石贴近符文石 → 石匣出现
9. 嵌入晶石 → 石匣打开

这个链条有明确的因果递进，每一步都有视觉反馈，符合"零意外编辑"的理念。GM Agent 在驱动这个谜题时表现出了一致性。

### 4.4 Amnesia 机制 — ⚠️ 主题与机制有张力

设计意图是"角色失忆 → 必须依赖外化笔记"，这是好的。但当前 validator 的过度严格（叠加 3.2 中的 bug）导致体验变成：

- 角色"失忆"是设定
- 玩家也"被迫失忆"因为 validator 不认历史
- 玩家只能把一切都写进 Notebook 才能通过验证

这其实强化了 Notebook 的核心地位，但代价是玩家需要在每个回合花大量精力在"如何措辞才能通过 validator"上，而非"我接下来想做什么"。

**建议**：
- 修复 `LastResolution` 注入后，validator 的 groundedness 检查会更合理
- 考虑在 validator prompt 中明确区分"世界事实"和"玩家推测"，对已结算的世界事实放宽要求
- 长期来看，可以考虑给 validator 提供一份"已确立事实清单"（从世界账本中提取），而非只依赖自然语言结算文本

### 4.5 交互发现 — ⚠️ 依赖玩家主动探索

当前交互 affordance 的发现完全依赖玩家主动 `look-around` + 仔细阅读。符文石的多个交互（查看纹路、触碰螺旋纹、触碰符文、捧泉水等）是逐步解锁的，这在设计上是好的，但玩家可能会漏掉。

**建议**：在 `look-around` 输出中，对"新出现的交互"给予视觉突出（如 `🆕` 标记），帮助玩家注意到世界变化。

### 4.6 时间系统 — ✅ 离散回合 + day/slot 清晰

Day 1 早晨 → Day 2 夜晚的推进节奏合理。每天 4 个 slot 的设置给了适度的行动压力。`rest-a-while` 作为"主动结束回合"的操作也很直观。

---

## 五、小问题与建议

| 类别 | 描述 | 严重程度 |
|:-----|:-----|:---------|
| Validator | `LastResolution` 未注入 validator prompt（见 §3） | 🔴 高 |
| UX | `look-around` 偶有输出格式异常（第一次调用时输出不完整） | 🟡 中（偶发） |
| Validator | validator 拒绝反馈中文化程度可以更高（当前混合中英文） | 🟢 低 |
| 设计 | 石匣打开后内容未展示——可能存在结算截断 | 🟡 中 |
| 设计 | 缺少"检查背包"类命令，玩家只能通过 `look-around` 看 InventoryItems | 🟢 低 |
| 文档 | `pmux game --help` 输出较长，首次使用需要滚动阅读 | 🟢 低 |

---

## 六、总结

TextAdv 的首版原型已经具备了扎实的骨架：PipeMux + StateJournal 持久化架构稳定、GM Agent 世界结算表现出色、Amnesia + Notebook 的核心玩法循环有明确的训练价值。

当前最影响体验的问题是 **Validator 上下文盲区**——`perception.LastResolution` 未被注入 validator prompt，导致玩家无法在事前推理中引用刚发生的世界事件。修复量很小（在 `BuildObservation` 中加几行），但对体验的提升是质变的。

修完之后，我很想再玩一遍，看看石匣里到底装了什么。
