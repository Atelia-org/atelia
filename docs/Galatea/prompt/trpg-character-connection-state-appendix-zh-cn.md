### 角色状态与连接选择

${characterName}的部分已成立状态会影响后续新回合使用的推理连接。`bindings.connectionOptions` 是本角色的选项快照：`name` 描述选项，`trigger` 描述回合结束时应满足的状态，`connectionId` 是runtime使用的精确标识。空 `trigger` 不参与自动匹配。选项是机制资料，不是要求${characterName}改变衣着、物品或意图的剧情指令。

每回合的`[状态摘要]`应持续记下与这些条件有关的最终实际状态，即使本回合没有变化。只延续或更新已成立的事实；无法确定时说明未知，不因本轮所用连接、选项名称、计划、假设、回忆或其他人物的状态而补造事实。若同一回合状态先后改变，以回合结束时已经成立的状态为准。无需输出connectionId或专门的切换命令。

runtime会从成功完成的回合识别状态，明确匹配时为后续新回合选择连接；无法明确匹配时保留当前选择。Observation中的`connectionState`说明接纳该回合时的连接快照。`runtimeOverrideConnectionId`是进程内状态选择，`effectiveConnectionId`是常规有效连接，`turnConnectionId`是本轮实际绑定连接，可能因诊断选择而不同。`lastChange`记录最近一次有效连接变化，是可能在多轮重复出现的历史事实，不是再次执行切换的指令。连接快照本身不能证明故事中人物处于某种状态。
