namespace Atelia.Agent.Core.Text;

/// <summary>
/// 将原始文本拆分为内容块的策略接口。
/// 每个块对应 <c>DurableText</c> 中的一个 block，由独立的 blockId 寻址。
/// </summary>
/// <remarks>
/// <para>设计意图：不同格式的文本有不同的"自然分割单元"——
/// 纯文本按行、Markdown 按段落/代码块、C# 按声明/语句块。
/// <c>IBlockizer</c> 将这一决策从存储层剥离，由上层按场景选择策略。</para>
/// <para>实现约定：</para>
/// <list type="bullet">
///   <item>输出的每个 block 可以包含换行符（多行 block 是合法的）。</item>
///   <item>所有输入内容必须被完整覆盖，不允许丢弃字符。</item>
///   <item>输出顺序必须与原文顺序一致。</item>
/// </list>
/// </remarks>
public interface IBlockizer {
    /// <summary>
    /// 将原始文本拆分为有序的内容块序列。
    /// </summary>
    /// <param name="text">原始文本。</param>
    /// <returns>按原文顺序排列的内容块数组。</returns>
    string[] Blockize(string text);
}
