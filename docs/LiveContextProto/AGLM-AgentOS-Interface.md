
# 当前阶段
目前还没有训练好的原生[AGLM](docs\LiveContextProto\AgenticGenerativeLanguageModel.md)，但可以先用ChatLLM在提示词工程和调度器的帮助下模拟。所以本篇内容主要是定义如何把AGLM的消息模板映射到经典的{System,User,Assistant/Model,Tool}四角色Chat模板上。

# AGLM

# 通知：
记录入Hitory，始终呈现，2档LOD级别。确保每条事件在历史中有且只有1份。

通用元数据:
- 收到时间戳: DateTimeOffset.Now.ToString("o")
- 产生时间戳(可空): DateTimeOffset.Now.ToString("o")

## 收到消息
通过ModelInput消息承载，对应于对话模型中的role="user"的消息，但这里的user其实始终是[Agent OS]本身。

信息:
- 发送者与我(LLM)的关系--发送者的名字: 你的开发者--刘世超
- 从什么渠道(可选): 控制台、即时通讯软件、语音识别、电子邮寄
- 正文: 代码围栏
- 正文摘要(可选): 代码围栏

# 同步工具调用结果
通过ToolResults消息承载，对应于对话模型中的role="tool"的消息。

适配LLM厂商API的必要信息:
- ToolName
- ToolCallId

Basic Result Content:
始终呈现的必要结果文本
- ToolExecutionStatus: Success, Failed, Skipped
- 基本结果

Extra Result Content:
有益但非必要的结果文本，用于帮助近期思路与行为的连贯性。
- 执行用时: human readable格式，自动调整单位来避免过大或过小的数字，这也适合LLM阅读。
- 补充信息

## [Notification]
随最新创建的ModelInput或ToolResults消息的创建，从队列中全部取出。
1. 上下文长度超过警告阈值

## [Window]
- 仅动态注入一份，默认不在History中存快照。
- [Window]本身无LOD级别，但其内各App的Window有受工具调用控制的渲染LOD级别，使得可以暂时聚焦一些Window而忽视其他的。
   各App的Window


记录：ModelInputEntry
呈现：ModelInputMessage


```md

```


记录：HistoryToolCallResult in ToolResultsEntry
呈现：ToolCallResult in ToolResultsMessage
```md
```

特长容器ToolCallResult
-工具调用结果。入History。创建时LOD。

通用容器ModelInput->User消息

History注入,创建时LOD,渲染时从LOD信息中多选一。
- 创建时间戳等Message级别的元信息。入History。
- Notification。入History。创建时LOD。
  - 外界对Agent所说的话

Window。不入History。渲染时各App内部自行LOD。产出Chapter。
渲染时间戳。

RawToolResult.WithHeading() -> ToolResultSection



Basic:
rawToolResult -> withNotifications -> withWindow

Detail:
rawToolResult -> withMeta -> withNotifications -> withWindow

|GUI窗口|WindowWindow|
|---|---|
|最大化|Full|
|常规|Summary|
|最小化|Gist|
