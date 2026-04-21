using System.Text;

namespace Atelia.Agent.Core.Text;

/// <summary>
/// 将带 blockId 的内容块序列渲染为 LLM 可消费的文本。
/// </summary>
/// <remarks>
/// <para>渲染产物是纯文本字符串，嵌入到 LLM 的 system/user message 中。
/// 每个 block 附带其 ID 标注，使 LLM 在后续 tool call 中能精确引用。</para>
/// <para>本类不依赖 <c>DurableText</c>，接受通用的 <c>(uint Id, string Content)</c> 序列，
/// 由调用方负责从具体存储类型转换。</para>
/// </remarks>
public static class TextRenderer {

    /// <summary>
    /// 渲染 block 序列为带 ID 标注的文本。
    /// </summary>
    /// <param name="blocks">按顺序排列的 (blockId, content) 序列。</param>
    /// <param name="options">渲染选项。传 <c>null</c> 使用默认值。</param>
    /// <returns>渲染后的文本，可直接嵌入 LLM 上下文。</returns>
    public static string Render(IReadOnlyList<(uint Id, string Content)> blocks, RenderOptions? options = null) {
        if (blocks.Count == 0) { return string.Empty; }

        var opts = options ?? RenderOptions.Default;
        var sb = new StringBuilder();

        for (int i = 0; i < blocks.Count; i++) {
            var (id, content) = blocks[i];

            if (opts.MaxBlocks > 0 && i >= opts.MaxBlocks) {
                sb.AppendLine($"... ({blocks.Count - i} more blocks omitted)");
                break;
            }

            string displayContent = content;
            if (opts.MaxContentLength > 0 && content.Length > opts.MaxContentLength) {
                displayContent = string.Concat(
                    content.AsSpan(0, opts.MaxContentLength),
                    $"… ({content.Length} chars)"
                );
            }

            switch (opts.Style) {
                case RenderStyle.Bracketed:
                    sb.Append('[').Append(id).Append("] ").AppendLine(displayContent);
                    break;
                case RenderStyle.Fenced:
                    sb.Append("--- block ").Append(id).AppendLine(" ---");
                    sb.AppendLine(displayContent);
                    break;
                default:
                    sb.Append('[').Append(id).Append("] ").AppendLine(displayContent);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 便捷重载：直接接受 <see cref="ContentBlock"/> 序列。
    /// </summary>
    public static string Render(IReadOnlyList<ContentBlock> blocks, RenderOptions? options = null) {
        var tuples = new List<(uint, string)>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++) {
            tuples.Add((blocks[i].Id, blocks[i].Content));
        }
        return Render(tuples, options);
    }
}

/// <summary>
/// 与存储无关的内容块表示，用于渲染管线。
/// </summary>
/// <param name="Id">块的唯一标识。</param>
/// <param name="Content">块的文本内容。</param>
public readonly record struct ContentBlock(uint Id, string Content);

/// <summary>
/// 控制 <see cref="TextRenderer.Render"/> 的输出格式。
/// </summary>
public sealed record RenderOptions {
    public static readonly RenderOptions Default = new();

    /// <summary>渲染风格。默认 <see cref="RenderStyle.Bracketed"/>。</summary>
    public RenderStyle Style { get; init; } = RenderStyle.Bracketed;

    /// <summary>最大渲染块数。0 = 不限。超出部分显示摘要。</summary>
    public int MaxBlocks { get; init; }

    /// <summary>单个 block 内容最大字符数。0 = 不截断。超出部分截断并附注原始长度。</summary>
    public int MaxContentLength { get; init; }
}

/// <summary>
/// 渲染风格。
/// </summary>
public enum RenderStyle {
    /// <summary>
    /// <c>[42] content here</c> — 紧凑单行格式，适合单行 block。
    /// </summary>
    Bracketed,

    /// <summary>
    /// <c>--- block 42 ---\ncontent here</c> — 围栏格式，适合多行 block。
    /// </summary>
    Fenced,
}
