
## 能力
LLM调用
切换LLM Provider
系统提示词
Lod历史消息
上下文渲染策略
Agent状态机
历史管理策略
工具调用
工具调用循环
IApp
Context反射，以Context为操作数的元能力。是Recap Maintainer, Meta Asker和多任务的必要基础能力。
SubAgent
Context Task Stack。类似函数栈,实现深度优先的树状思考。再补充搁置任务并从深层跳转返回的能力。

*规划中* 流式解析和结果注入的小工具: 流式解析，中断输出，拼接结果到模型输出中，继续补全输出。比tool calling更轻量。许多模型和Provider都支持补全输出功能。
*规划中* HintMark: 制定低侵入和语法噪音的规范格式，来让模型主动记录“我要记住.../我想要.../我应该.../下次再遇到...我就...”。这不是即时执行的工具调用，但是会被AgentOS识别并捕获，进而驱动外部记忆存储和生成自我训练的SFT语料。
*规划中* 注入触发器: 和前面两个机制有重叠，但又有特化。核心是触发条件+注入信息。例如: “5分钟后我要去检查水壶/猫猫是一种非常凶猛的动物，再见到我要保持距离/离开饭馆时我应该检查是否落下了东西”。这些由AgentOS执行，生成小型LLM驱动的模式识别器，触发后注入设定的信息。

## 基本App
TextEditor: Replace, ReplaceSpan
FileText: OpenFile, CloseFile。编辑即时落盘，文件渲染始终最新，查看和编辑的都是文件。
BufferText: CreateBuffer, LoadFile, SaveFile, DeleteBuffer, OpenFileInBuffer(=CreateBuffer+LoadFile)。本质上查看和编辑的是Buffer，通过Load/Save与文件关联。
MemoryNotebook: 实现上，似乎应该是一个FileText。
TerminalCommand: 执行终端命令。TODO: 实现方式需要进一步调研。
Dice: 产生目标分布的随机数，主要用于帮助LLM Agent能分析出概率性测率但分析不出确定性策略时做行动采样。
Calculator: 计算表达式，有多种底层实现候选。处理简单即时计算问题。
C# REPL: 用临时程序处理更复杂的问题，以及拓展功能边界。
Python REPL: 用临时程序处理更复杂的问题，以及拓展功能边界。用Pythonnet+CPython实现。
MCP Connector: 连接并包装MCP服务。

## 业务逻辑
业务层输入输出交互逻辑
业务层工具
业务层App
业务层系统提示词
业务层历史管理策略
业务层上下文

# 已知需求

## Wait And Work
传统Agent。等待输入，工具调用循环，再次进入等待状态。

## Sense Think Act Loop
本项目的阶段目标。持续的Sense-Think-Act循环，时间流逝和当前系统状态成为闲时的输入。依赖于RecapMaintainer来控制上下文总长度。需要过程性目标，例如探索、积累、修身、齐家、降低风险、总结经验。MetaAsker能帮助其更好的持续思考。配合外部记忆系统、各种必要工具、目标-进度-挑战追踪辅助系统，实现长期连续自主思考与行动。

## Recap Maintainer
SubAgent。Rolling Summary机制。对Recap文本和其他Agent的MessageHistory进行编辑，负责将最旧的输入输出消息轮次摘要后转移到Recap文本中，并按需选择低后效性的次要信息从Recap文本中移除来控制其总长度。直到将MessageHistory的总长度缩减到目标范围。处理记忆类HintMark。

## Meta Asker
SubAgent。以其他Agent的MessageHistory作为源操作数进行分析，在必要时介入生成“苏格拉底诘问”式元问题。生成的问题由AgentOS以合适的方式注入目标Agent上下文中。

## Corpus Generator
专用Agent。以提示词引导的特定模式，来批量生成SFT语料。用于后续AGLM微调实验。

## Teacher
专用Agent。以提示词引导的方式，让大模型驱动的Teacher对小模型驱动的Student进行提问评估、调查小模型的知识与能力边界、给与讲解和指导。以高效的沿着知识与能力的边界，为小模型生成SFT样本，因材施教。Teacher建模为主导Agent，而Student则建模为一个被Teacher调用的Tool，通过工具调用循环进行。一方面是大模型初始能力强得多，更能完成引导流程的职能。另一方面，微软提供“按请求次数计费”的LLM调用服务，一条User消息一次计费，不限制工具调用循环轮数。这样适当提示词工程和上下文管理后，一次计费就能进行多轮授业问答。

## Student
研究中，实验性Agent。需要实现“兴趣机制”和模型具备“不知之知”元能力，才能实现。兴趣定义为在有价值领域取得收获的预期，这里价值和收获的含义都是多方面的。需要对模型行为进行即时评估和长期跟踪。取得进展就“兴奋”(积极信号)，驱动未来获得更多注意力和行动时间的配额。降低已经充分掌握和还不具备基础的任务领域的时间配额，将活动集中在“跳跳脚够得着”的那些知识与能力边界领域。这样的Agent，将自主选择探索什么、提问什么、生成什么SFT语料。在这个方向上，[MIT SEAL Self-Adapting Language Models](https://arxiv.org/abs/2506.10943)已取得概念验证，[项目git repo](https://github.com/Continual-Intelligence/SEAL)。其实人类的自我总结、思维反刍、反事实设想、暗自下定决心“我下次要...我以后应该...”的机制，等效于LLM Agent “自己给自己写训练语料”。

## ReReader
研究中，实验性Agent。让已完成训练的LLM重读经典文献，带着通读过一遍的状态再度文献，进行信息间的交叉模式识别，预期会有新的感悟，也就是生成新SFT语料。关于预想效果，至少应该会包括在通读完各种语料一遍后(预训练)，通过多方印证(总体最大似然估计)，再读时可以更好的识别出噪声(谬误)，进而生成纠偏语料。以及发现初次阅读时没能学到的深层模式。
