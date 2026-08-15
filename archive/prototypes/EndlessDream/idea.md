# Endleass Dream

本质上这是一个LLM训练语料生成器，目的是尝试一种非 Instruct / Chat 的 Userless LLM 范式，让LLM内化最基础的主体性。
这还不是 RL Gym，无法直接驱动 Agent 在环境中刷奖励，更接近一种模型蒸馏和能力形式迁移，把典型 Instruct LLM 中 Assistant 角色的分析能力文学写作能力迁移为塑造一个和经典 Assistant 平级的角色 -- `我`。

## 核心做法

用规则代码驱动 Instruct LLM 组成 pipeline 来分工续写一个无尽的第一人称梦境。

**World-Log**:
  - 为了避免梦境世界太过荒诞而维护的豆腐账。
  - 使用第三人称撰写，世界 event log。
  - 具有软长度上限。

**Stream-Of-Consciousness**:
  - 由 Sense / Thinking / Intent 组成的序列。
  - `我`这个角色在梦境中的意识流豆腐账。
  - 使用第一人称撰写，主观体验。
  - 具有软长度上限。
  - 受信息可见性影响。
  - 有一个持续维护的 Belief 集合（文本形式存在）

总体结构脱胎于TRPG的GM-Player结构，抽象GM维护的**World-Truth**起到了世界模型和虚拟环境的作用，抽象Player维护的**Sense-Thinking-Intent**则是核心产物。

GM先生成初始**World-Log**
loop
  GM 向 Player 投影新一轮的 Sense
  Player 续写 **Stream-Of-Consciousness** 中的新一轮 Thinking 和 Intent
  GM 根据最新的 Intent 更新 **World-Log**

在 loop 中的每个环节都由 Instruct LLM 实现核心功能，实现上可以是一个多步骤 pipeline 。可以有闭环反馈，比如有检查器来为生成器提供发现的问题和修改意见。

## 与 Instruct LLM 的核心区别

Instruct LLM 的潜空间状态机是开放的，从user消息获取临时性意图，然后迅速归于新的无意图状态停机。
Endleass Dream 则试图构造一个自我闭合的潜空间状态机，始终有内化的动机，永远不会停机。
