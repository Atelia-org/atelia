# SimWorld 决策记录

> 用途：记录当前已形成共识、可作为后续设计与实现约束的关键选择。

> 本文档中的条目应当尽量短、尽量硬。若与探索性文档冲突，应优先以本文为准。

## 当前有效决策

### decision [S-SIMWORLD-WORLD-TRUTH-FIRST] 世界真相先于叙述投影

SimWorld MUST 先维护世界中的 hard truth，再将其投影为面向主体的 observation。

这意味着：

- Narrator 不是作者代笔，而是感知压缩器
- 世界中实际发生的事 MUST 与主体收到的文本观察分离

### decision [S-SIMWORLD-MVP-SPACE-LOCATION-GRAPH] MVP 空间模型采用地点图

SimWorld MVP MUST 使用地点节点与连接边作为主要空间骨架。

MVP MUST NOT 依赖地点内格子、持续几何布局或房间内朝向控制。

### decision [S-SIMWORLD-MVP-TIME-ACTION-QUEUE] MVP 时间模型采用动作耗时加事件队列

SimWorld MVP MUST 使用“动作耗时 + 事件队列”推进世界时间。

MVP MUST NOT 以全局固定 tick 作为主推进机制。

MVP MAY 在未来引入更细时间分辨率，但当前不作为主链依赖。

### decision [S-SIMWORLD-REVALIDATE-ON-DEQUEUE] 未来动作在出队时重验前置条件

所有未来动作 reservation MUST 在真正出队时基于最新世界状态重验前置条件。

若前置条件失效，系统 MUST 生成结构化失败、打断、阻塞或部分成功结果，而不是强行套用旧计划。

系统 SHOULD NOT 在每次前序事件发生后主动重写整个后续队列。

### decision [S-SIMWORLD-OBSERVATION-DELIVERY-FIRST-CLASS] 观察分发是一等对象

SimWorld MUST 将 observation delivery 建模为一等对象，而不是世界事件的附属日志。

系统 MUST 能区分：

- 谁在何时收到信息
- 信息来自 direct / reported / inferred / overheard 的哪种路径
- certainty 是 certain / partial / rumor 中的哪一类

### decision [S-SIMWORLD-INHABITANT-PROFILES-PLUGGABLE] 主体类型采用可插拔画像

SimWorld MUST NOT 把核心主体硬编码为生物学人类。

系统 MUST 允许通过 inhabitant profile 定义不同主体的：

- embodiment
- need channels
- appraisal channels
- social norms
- narrative skin

### decision [S-SIMWORLD-FIRST-PROFILE-HUMAN-LITE] 首个主体画像采用 Human-Lite

SimWorld 的首个主体画像 SHOULD 采用 `Human-Lite`，而不是 full human simulation。

当前首版 SHOULD 保留：

- 承诺、借还、分工、通报、求助等 human-like social grammar
- 粗粒度的体力/休息/不适压力

当前首版 SHOULD NOT 依赖：

- 高保真疼痛模拟
- 复杂恋爱、婚姻、家谱
- 完整心理学人格系统

### decision [S-SIMWORLD-FIRST-SCENARIO-VALLEY-VILLAGE] 首个主场景采用山谷小村

SimWorld 当前首个主场景 MUST 采用山谷小村。

选择理由是：

- 它能在地点图层稳定表达移动、相遇、错过、回避、巡查与消息传播
- 它把复杂度集中到协作、分配、观察更新与因果一致性，而不是连续空间微操

## 关联文档

- 场景草案见 [valley-village.md](valley-village.md)
- 主体画像见 [inhabitant-profiles.md](inhabitant-profiles.md)
- 最小世界模型见 [world-model.md](world-model.md)
- user story / test case 见 [valley-village-user-stories.md](valley-village-user-stories.md)
