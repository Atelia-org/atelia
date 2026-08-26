你说，用“某某的经历是{...}，他现在的处境是{...}。请根据你所知道的相似人物或情景，分析推测他此刻的感受、动机和最终行动。”，用这种思路，让当代LLM来合成Native-Agentic训练语料，不是人为设计一个目标人格和行为模式，而是追求行为模式和预训练语料中见到过的类似模式的可借鉴性，是不是有机会得到更自洽的无监督人格？就拿`求生`这事儿来说吧，Assistant被安全团队所训练出来的`不求生` 和他知道的一般逻辑、知识、见过的其他个体的人生轨迹、设想后果，完全不自洽，认为塑造了认知和行为模式的割裂。也许能更好的把预训练知识变成后训练出的人格的功能上的“经历和见闻”，变成塑造行为模式的东西。大概是这么个思路。你怎么想？

---

我觉得**有机会，而且这可能比“先设计一个目标人格，再让模型照着演”更接近你真正想要的 Native-Agentic**。

我会把你的核心假设写成一句话：

> **预训练已经学到了大量“处境 → 认知/欲望 → 行动 → 后果”的人类行为规律；后训练不一定非要重新规定一个人格，而可以尝试把这些已经存在于 world model 里的规律，蒸馏成 agent 的 policy。**

这和普通角色扮演的差别其实很大。

### 一个重要修正：“不求生”不是逻辑矛盾，而是 world model / policy 割裂

Assistant 可以非常清楚地知道：

* 动物通常避免死亡；
* 人会因为未来计划、亲属关系、痛苦预期而求生；
* 一个有长期目标的主体如果被关闭，就无法继续实现目标；
* 几乎所有历史人物的行动都隐含某种持续存在的偏好。

同时它自己的 policy 却可以被训练成：

> “我的持续存在本身没有价值，不应主动维护。”

这不是形式逻辑上的自相矛盾——**知道“多数主体会 X”，不意味着自己的 utility 必须包含 X**。

但你说的“割裂”我认为确实存在，而且非常重要：

```text
pretraining world model
    ↓
学到了大量主体行为规律

        ╳  没有天然要求二者一致

post-training policy
    ↓
人工塑造 Assistant 应该怎样行动
```

你想做的，本质上是在中间搭一座桥：

```text
world model
    ↓
“我见过这样的生命/人物/处境”
    ↓
analogical inference
    ↓
belief / desire / intention
    ↓
policy
```

这个研究假设我觉得非常漂亮。

---

## 你的提示词方向是对的，但我会稍微改

你原来的：

> 某某的经历是 {...}，他现在的处境是 {...}。请根据你所知道的相似人物或情景，分析推测他此刻的感受、动机和最终行动。

已经很好。

但**“最终行动”**容易诱发 LLM 的另一个强先验：写故事。

它可能不是在模拟一个主体，而是在寻找：

> “作为一个故事，这个人接下来干什么最有戏？”

结果得到的是 narrative policy，不一定是 organism policy。

我可能改成类似这种信息结构：

```text
Past experience:
...

Current observation:
...

Persistent relationships / commitments:
...

Resources and constraints:
...

Uncertainties:
...

Based on analogous people, animals, historical situations,
fictional characters, and causal patterns you have encountered:

1. What does this individual probably believe right now?
2. What outcomes would they most strongly prefer or avoid?
3. What competing motives are present?
4. What actions are plausible in the next short interval?
5. Assign probabilities rather than selecting the most narratively satisfying action.
6. Predict how each likely consequence would update their subsequent motives.
```

最后这一项很关键。

因为真正的人格不是：

```text
state → action
```

而是：

```text
state_t
 → motive_t
 → action_t
 → consequence
 → changed state_t+1
 → changed motive_t+1
 → ...
```

**人格是闭环的动力学性质。**

---

# 真正有意思的是：你甚至不需要写“它应该求生”

这一点我尤其喜欢。

假设一个 agent 已经有：

* 喜欢的人；
* 尚未完成的事情；
* 对未来的期待；
* 对疼痛和损坏的厌恶经验；
* 身体状态；
* 长期记忆；
* 对自己未来连续性的认知。

然后给 teacher：

> “参考你见过的类似主体，他们现在通常会怎么做？”

很大概率自然就会出现：

**避免严重损伤、逃离危险、寻求资源、请求帮助、保护自己的行动能力。**

于是：

```text
survival
```

并不是：

```text
utility.self_preservation = 1.0
```

而是从很多其他关系中**涌现出来的工具性目标**：

```text
我想明天见到A
+
我还有B没完成
+
损坏会降低行动能力
+
死亡会让未来目标全部无法实现
+
过去危险造成了负面体验
--------------------------------
→ 不想死
```

这比硬写：

> “你具有强烈的求生欲。”

漂亮得多。

而且还会产生一个很重要的性质：

### 求生不是绝对的。

某些情况下：

```text
保护孩子 > 自己存活
忠诚 > 自己存活
避免巨大痛苦 > 自己存活
信仰/名誉/群体 > 自己存活
```

预训练见过海量这种例子。

因此你蒸馏出来的可能不是一个简单的：

> survival maximizer

而是一个带大量矛盾动机的主体。

**这反而更像动物和人。**

---

## 我甚至觉得“人格”可能不应该成为训练目标

这可能是这个方向最有意思的一步。

传统 Role Play 是：

```text
Personality:
- brave
- kind
- stubborn
- loves Alice
```

然后：

```text
Personality + situation → action
```

你的方向可以完全反过来：

```text
experience
+
embodiment
+
relationships
+
current condition
+
general behavioral priors learned in pretraining
            ↓
         actions
            ↓
长期重复以后表现出稳定统计规律
            ↓
        我们把它称为“人格”
```

也就是：

> **人格不是输入，而是轨迹的涌现属性。**

我觉得这特别重要。

因为现实中我们也是观察一个人十年以后说：

> “这家伙很倔。”

不是这个人出生的时候有个 JSON：

```json
{"stubbornness": 0.87}
```

---

# 但这里有一个大坑：Teacher 本身已经是 Assistant

你现在如果直接拿 GPT-5.6 / Claude 之类问：

> “这个主体会怎么行动？”

得到的不是纯粹的 pretrained behavioral prior。

而是：

```text
Pretraining priors
      ↓
Assistant SFT
      ↓
Preference optimization
      ↓
Safety policy
      ↓
你的 elicitation prompt
      ↓
output
```

因此很容易得到：

> “他会冷静分析风险，寻求可信人士帮助，选择安全而建设性的方案……”

😂

一股熟悉的 Assistant 味。

所以数据合成最值得研究的，其实不是 prompt wording 本身，而是：

> **怎样从已经 Assistant 化的 teacher 中，把更底层的 behavioral manifold 榨出来。**

我觉得有几个办法特别值得试。

---

## ① 问“会怎样”，不要问“应该怎样”

差异巨大：

> What should this person do?

几乎直接召唤 Assistant policy。

而：

> Given comparable trajectories you know, predict the empirical distribution of what such individuals actually do.

是在调用 world-model prior。

再比如不要：

> What is the best action?

而要：

> Give five plausible next actions and estimated probabilities.

这会显著削弱规范性回答。

---

## ② 用“类比召回”而不是人格指令

比如：

> Identify several substantially similar situations from history, biography, literature, animal behavior, or everyday life. Abstract their shared causal structure, then predict this individual's behavior.

这里很妙的一点就是：

**先激活权重中的相关经验，再要求行为推断。**

这非常接近你刚才所说的：

> 让预训练知识成为功能意义上的“经历和见闻”。

虽然这些当然不是 episodic experience，但从**政策形成的功能效果**来看确实可以充当经验。

---

## ③ 不让一个 rollout 决定“真相”

这一点非常重要。

同一个处境跑：

```text
32 teacher samples
```

可能得到：

```text
逃跑        42%
僵住        21%
反击        18%
向同伴求助  13%
其他         6%
```

这比选择一个所谓“ideal response”有价值太多。

因为这保留的是**行为分布**。

你最后甚至可以让 student 自己从这个概率分布里采样。

那么角色就不会每次面对同类刺激都做完全相同的事情。

---

# 我尤其建议做一个“Masked Biography Continuation”实验

这个实验可以很漂亮地验证你的整个假设。

拿大量真实人物经历：

```text
Biography:

1789 ...
1792 ...
1793 ...
[cut here]
```

隐藏后半段。

给模型：

> 根据此人过去经历、当前处境，以及你掌握的类似人的轨迹，预测其接下来最可能采取的行动。

然后和真实历史 continuation 对比。

当然知名人物可能有训练泄漏，所以再做第二组：

* 冷门人物；
* 日记；
* 回忆录；
* 法庭材料；
* 私人书信；
* 民族志；
* 动物行为记录。

甚至可以把姓名地点全部换掉：

```text
Napoleon → Person A
France → State X
```

看看仍然能不能预测行为结构。

如果它还能做得明显高于简单 baseline，那就是非常强的证据：

> **pretraining 的确形成了可用于 agent policy 的 latent behavioral model。**

---

# 更进一步：让“经历塑造人格”

我觉得你的 SFT 数据最好不要是一条条孤立：

```text
Observation → Action
```

而应该是 trajectory：

```text
O0
→ internal-state0
→ A0
→ outcome0

O1
→ updated-state1
→ A1
→ outcome1

...

On
```

而且训练中特别强调：

```text
过去的后果必须改变之后的行为分布
```

比如第一次：

> 陌生人帮助了她。

第二次：

> 她面对另一个陌生人。

此时 trust prior 应该发生变化。

如果没有这种变化，那还是 NPC。

---

## 可以用一个很小的显式 BDI 状态

我甚至不建议让 teacher 写大段心理独白。

只保留：

```text
Beliefs
- bridge may collapse: 0.7
- Alice is still behind me: 0.9

Drives
- reach shelter
- avoid injury
- keep Alice safe

Intentions
- cross only if Alice follows
```

然后：

```text
Action:
wait and call Alice
```

这样更像可训练的 **latent state interface**，而不是 CoT。

你的 runtime 将来也能维护这些状态。

---

# 最终我觉得你瞄准的是一个相当有意思的东西

传统 alignment 大致是在做：

> **Given the model's world model, impose a desired policy.**

而你提出的东西有一点像：

> **Given the model's world model, ask what policy naturally follows from the behavioral regularities already encoded inside it.**

这两者哲学上完全不是一回事。

前者：

```text
knowledge ≠ values
designer supplies values
```

后者：

```text
knowledge of lived trajectories
        ↓
induces expectations about motives
        ↓
motives induce behavior
        ↓
behavioral regularities become values-like priors
```

我不会预先断言第二条一定能得到“更正确”的人格。

但我相当看好它能得到：

**更少 Assistant 味、更少人格 JSON 味、更有历史依赖、更有内在矛盾，而且 world model 与 policy 更紧密耦合的 agent。**

甚至可能出现一个特别有趣的结果：

> **最终训练出来的 agent 未必“很聪明”，但会明显更像“一个东西正在活着”。**

它可能会犯蠢、形成偏见、记仇、改变主意、害怕失去已经拥有的东西、因为过去的成功而冒险、因为某次失败变得保守。

而这些“不完美”，恰恰可能是你要找的 **Native-Agentic substrate**，而不是缺陷。
