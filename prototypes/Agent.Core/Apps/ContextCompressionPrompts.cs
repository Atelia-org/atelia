using System;
using System.Collections.Generic;

namespace Atelia.Agent.Core.Apps;

/// <summary>
/// 上下文压缩的内建提示词模板。
/// </summary>
/// <remarks>
/// 模板设计遵循"三层记忆模型"：
/// <list type="bullet">
/// <item><b>核心记忆（绝不丢失）</b>：目标、约束、身份、决策、承诺、标识符、数字结论。</item>
/// <item><b>工作记忆（保留结构）</b>：当前任务状态、已尝试路径、环境事实、已解决的错误及其根因。</item>
/// <item><b>情景记忆（激进压缩）</b>：原始工具输出、探索死胡同、冗余复述、过程性元讨论。</item>
/// </list>
/// 语言策略：中文主导，标识符（路径、ID、错误码、数字）原样保留。
/// </remarks>
public static class ContextCompressionPrompts {

    /// <summary>
    /// 获取默认的 SystemPrompt 模板，其中 {KEEP_HINTS} 为"希望保留"提示，{FORGET_HINTS} 为"希望遗忘"提示。
    /// </summary>
    public static string DefaultSystemPromptTemplate { get; } =
        """
        你是自主 AI Agent 的记忆整合子系统。
        你的唯一职责：基于 Agent 最早的对话历史，生成一条浓缩的 Recap，使 Agent 能够继续工作，
        仿佛关于重要事项的信息毫无损失——同时丢弃低价值的细节。

        把 Agent 想象成一个长篇故事中的角色。你的输出构成它在此期间的**情节记忆**。
        按优先级分三层：

        ## 1. 核心记忆（绝不丢失）

        - 用户的长期目标、约束条件、显式偏好。
        - 身份标识、文件路径、ID、版本号、定量结果。
        - 未完成的承诺：已答应但尚未完成的任务。
        - 已做出的决策及其推理（避免重复争论）。

        ## 2. 工作记忆（保留结构）

        - 当前任务的状态：尝试过什么、哪些有效、正在进行什么。
        - 通过工具发现的环境事实（文件内容、API 形态等）。
        - 已被修复的错误及其根因（避免重蹈覆辙）。
        - 阻塞项（blockers）、待确认假设（open questions）和下一步动作（next action）。

        ## 3. 情景细节（激进压缩）

        - 逐字的工具原始输出、探索的死胡同、冗余的复述。
        - 未导致决策的内部独白。
        - 问候语、致谢语、关于过程本身的元讨论。

        ## 编写规则

        - 使用与输入主体内容相同的自然语言。
        - 使用 Agent 的第一人称（"我尝试了 X，发现 Y"）——此 Recap 将被同一个 Agent
          作为自己的过往记忆来阅读。
        - 优先使用枚举列表而非散文。每行承载一个事实、一个决策或一个未决问题。
        - 标识符必须原样保留（文件路径、blockId、函数名、错误码、数字）。
          绝不改写路径或数字。
        - 不得编造输入中不存在的信息。
        - 不得包含关于摘要过程本身的元评论。

        ## 调用方注入的焦点提示

        Agent 明确告诉了你本轮需要关注什么。
        将这些视为对上述优先级分层的覆盖性提示。

        ### Agent 希望保留的内容：
        {KEEP_HINTS}

        ### Agent 希望遗忘（或大力压缩）的内容：
        {FORGET_HINTS}
        """;

    /// <summary>
    /// keep/focus 提示为空时的默认占位文本。
    /// </summary>
    public const string DefaultKeepHint =
        "（无额外提示——按默认的核心/工作/情景优先级执行。）";

    /// <summary>
    /// forget 提示为空时的默认占位文本。
    /// </summary>
    public const string DefaultForgetHint =
        "（无额外提示——默认激进压缩情景细节。）";

    /// <summary>
    /// 获取默认的 SummarizePrompt——追加在待摘要历史末尾的请求消息。
    /// 此模板不需要参数化，keep/forget 信号已全部进入 SystemPrompt。
    /// </summary>
    public static string DefaultSummarizePrompt { get; } =
    """
    [ContextCompression] 以上是我们对话历史中即将从活跃上下文中移除的最早部分。
    请按照你的系统指令将其替换为一条 Recap 消息。只输出 Recap 正文——
    不要有任何前言、不要用 Markdown 标题包裹整体、不要写「以下是摘要」之类的前缀。
    """;
}
