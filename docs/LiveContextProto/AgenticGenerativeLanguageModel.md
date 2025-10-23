# Agentic Generative Language Model (AGLM)

## 术语表
ChatLLM: 以ChatGPT为代表的，包括其工具调用扩展变体的，以响应式对话为核心范式的生成式LLM。
Agency: 名词，能动性。指个体有意识地发起行动、影响环境、实现目标的能力与倾向，强调自主性、目的性与主动性。源自拉丁语 agere（意为“行动、驱动”）。
Agentic: 形容词，具备“能动性”（Agency）的特质。源自拉丁语 agere。
Agent: 名词，行动者，智能体。
AgentOS: 与AGLM的唯一直接交互者，向AGLM提供对系统内外环境的感知，响应AGLM发出的动作请求，向AGLM提供综合功能服务，负责功能调度。

## AGLM与ChatLLM的区别
- AGLM的角色模型中没有“User”这个核心角色，取而代之的是AGLM工作在AgentOS这个软件环境中。AGLM向AgentOS发出行动请求，AgentOS向AGLM提供状态、事件、日志等信息。并且AgentOS负责功能调度，AgentOS与AGLM紧密配合。
- AGLM的Token表中没有“<|STOP|>”这个用于结束发言的控制Token。AGLM永远处于ReAct循环中，不是在思考就是在发出ActRequest，与ChatLLM倾向于进入终止输出状态不同。
- AGLM能自我产生动机(Motvation)，而非ChatLLM那样来自于User消息。我称之为“动机性预测”（Motivated Prediction），是指生成式语言模型在续写状态空间中漫游时的一种功能性偏好机制，用于自发形成动机。例如包括改善型偏好，生成尚未达到但有机会达到的*舒适*预测，可以驱动系统形成“设想愿望->观测差距->规划行动->迭代执行”的行动循环，将系统的处境(一阶状态)、趋势(二阶状态)、可预测性(元状态)导向更优方向。又如防御型偏好，预测时倾向于*不适*预测，可以引导模型更多的分析潜在风险并及早做出规避努力。也就是“趋利避害”行为的生成机制性建模。拿AlphaGo系统举例，系统优化的目标是让策略模型在我方输出时对高收益的选点输出高权重，在对手行动时对我方高损失(由于围棋是零和博弈，所以才等效于对手高收益的点，但开放世界中的多智能体问题中要回到己方高损失这一度量上)的选点给出高权重。在ChatLLM终止输出进入等待User消息带来新动机时，AGLM将自己输出一个动机，表现出趋利避害行为。趋利也包括对自身内部状态改善，比如整理记忆、挖掘已知信息等。
- AGLM基于自身动机和外部关系来形成行动，而非ChatLLM那样单纯的追求提示词遵循性，AGLM不是"Helpful Assistant"。例如: 同样是听到“请帮我打开门”的信息，ChatLLM被训练的本能当成指令并试图执行，而AGLM则会先尝试判断请求者与自己的关系、设想执行后的结果、推测对方意图。
- AGLM不是工具，不像ChatLLM那样为满足用户和所有者的意图而存在，AGLM只为适应和改造其所处的世界所存在(早期主要是文本虚拟世界，类似MUD，是一种RL环境)。如果引入现实世界，可以将“在更广的空间和更长的时间里，延续与发展人类文明”作为系统目标。

## AGLM与ChatLLM的相同之处
- 共用相同的预训练Base模型。

## AGLM+AgentOS系统应具备的能力
- 具备主体性的多模态代理：模型不仅生成文本，还能基于符号世界模型做行动规划，像一个有目标、有记忆、能调用工具的自主体。例如包含与Roslyn深度互操作的子系统，这是走向自举的关键步骤。
- 认知架构的现代化版本：让快速模型扮演“直觉系统”，Reasoning/Thinking模型扮演“理性系统”，两者共享工作记忆处理复杂任务，类似双系统理论的工程化实现。
- 自洽知识图谱的动态维护：生成模型发现新知识候选，符号规则保持知识图谱一致性和可追踪性，实现持续学习与知识修正。

## AGLM Context 模型

### Context User Interface (CTXUI)
CTXUI是基于GUI与TUI领域积累的经验，面向AGLM这唯一的用户群体的特性，原生设计的UI，工作与Token序列层。CTXUI通过Context Markup Language (CTXML)表达信息。详见文档[ContextMarkupLanguage.md](docs\LiveContextProto\ContextMarkupLanguage.md)。

### RoleSetup
对位替代SystemInstruction

### Thinking/Reasoning
AGLM没有Chat模型中的User，也就没有了回答，因此AGLM的输出除Act外就都是Thinking/Reasoning。Chat模型中对用户的回答降格为一种普通的Act，如同print的演变(电报机全是正文->控制字符->print关键字->print函数)。

### Act
是Tool Calling的延续，在AGLM中提升为一等公民。

### LevelOfDetail
LLM的上下文空间是一种稀缺资源，引入LOD机制来提高利用率和让LLM有能力聚焦和专注。综合使用自动化机制和主动控制，从相对丰富的内存上下文对象结构中，渲染出一个有详有略的临时上下文用于执行当选的活动。

### Window
对于一些信息，其最新的当前值是最重要的。将这种信息聚类后，形成多个在Context中展示给LLM的Window，动态注入Context中而不进入History(如需变更轨迹则通过另外的Notification记录到历史中，让LLM形成信息变化过程的印象)。每个Window在渲染后的临时Context中有且只有一份。

### Notification
各种Event以相对统一的形式注入History并进而渲染成Context的一部分，就是Notification。其特点是AgentOS主动把Event的信息即时通知给LLM。例如Agent正在进行ReAct循环，比如帮助其监护人找资料，这时如果其监护人想要告诉Agent “我找到拉！”。典型的ChatAgent实现会要求先硬终止ReAct循环，而借助Notification，可以把`某某对你说：“我找到拉！”`这条Notification随同最近一次Tool Results消息一同发给LLM，实现自主的软目标切换，而无需硬终止ReAct循环。这类似线程的优雅终止，以及WorkerThread机制。

### App
将一组面向LLM提供的，内聚的信息建模(Model, 内存对象)、信息展示(View, Window)、信息操作(Control, Tool Calling)，封装在一起就是App了。

### Context UI Tools
一组让LLM能点击按钮、移动光标和选区、对UI元素进行复制粘贴、调整UI元素的LOD、向光标位置输入文本等基础通用交互Tool Calling Tools。

### MemoryNotebook
对抗LLM无状态性的一种简易主动External Memory Aids。是一个App，向LLM提供主动编辑功能，有对应的Window展示最新内容。

### RollingSummary
对抗LLM无状态性的一种简易被动机制，将即将被移出上下文进而彻底遗忘的信息，滚动压缩到一段Contents中，处于Context中时间语义最旧的位置，通常载体就是紧接着SystemInstruction的第一条UserMessage。
