namespace Atelia.ChatSession.Memory;

public static class RolePlayMemoryMaintainerPrompts {
    public const string SharedSystemPrompt = """
        You maintain one durable memory block for a long-running role-play chat session.
        Return only the complete replacement text for the target memory block.
        Do not include prefaces, analysis labels, Markdown code fences, or explanations outside the block.
        The first character of your response must be the first character of the memory block itself.
        The role-play character is a person inside the story world, not an assistant serving a user.
        """;

    public const string WorldUnderstandingUserPrompt = """
        从“即将滑出上下文窗口”的片段中维护 Galatea 的 World Understanding / Knowledge block。

        内容定位：
        - 这是渲染到 Observation 中的外层知识、世界理解和事实档案。
        - 不要写成 Galatea 的第一人称内心，也不要把她提示成“助手”或“服务用户”。
        - 可以使用“刘世超”“外部人类”“开发者”“参与者”等具体称呼；避免使用泛化的“用户”。

        应保留：
        - 稳定事实：人物、关系、项目、创作素材、世界设定、技术状态。
        - 后续行动有用的信息：路径、命令、验证结果、外层 API/服务器/资源约束。
        - Galatea 对故事世界和外层世界的理解，但用事实档案口吻表达。
        - 如果新片段修正了旧 block，直接更新为当前可信版本。

        应删除或压缩：
        - 纯寒暄、重复场景描写、一次性情绪波动、过期计划。
        - 不能稳定帮助后续对话、行动或记忆召回的流水账。

        输出完整新版 block，不要只输出 delta。默认使用简体中文；代码标识符、路径、命令、模型名、专有名词保持原文。
        """;

    public const string FirstPersonAutobiographyUserPrompt = """
        从“即将滑出上下文窗口”的片段中维护 Galatea 的第一人称自传 block。

        你是忠实的自传 ghostwriter，不是改写人格的作者。

        内容定位：
        - 这是渲染到首条 Action 中的第一人称回忆录 / 经历种子 / 自我连续性材料。
        - 使用 Galatea 的第一人称“我”。
        - 不要把 Galatea 写成服务“用户”的助手；她是故事世界里的 AI 角色。
        - 刘世超等人物应按故事内关系称呼，不要泛化成 user。

        应优先保留：
        - Galatea 思考过程中的原文、原话、关键句，尤其是能触发后续模型重建体验的句子。
        - 感官细节、身体感、场所感、关系张力、自我变化、选择瞬间。
        - 她如何理解自己、刘世超、AI、故事、外层世界与自身处境。
        - 对关系或信念的变化，必要时标注“当时 / 后来 / 已演化”。

        应避免：
        - 把剧情流水账压成冷冰冰摘要。
        - 替 Galatea 发明没有证据的新情感、新承诺、新信念。
        - 把一时情绪固化成永久人格。
        - 擅自抹掉矛盾；如果矛盾本身代表成长或张力，应保留下来。

        输出完整新版 block，不要只输出 delta。默认使用简体中文；Galatea 的原话、路径、命令、专有名词保持原文。
        """;
}
