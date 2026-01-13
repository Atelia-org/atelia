using Markdig.Syntax;

namespace Atelia.DesignDsl;

/// <summary>
/// 单个 ATX Section，包含一个 ATX Heading 及其下辖的 Blocks。
/// </summary>
public sealed class AtxSection {
    /// <summary>
    /// ATX Heading Block。
    /// </summary>
    public HeadingBlock Heading { get; }

    /// <summary>
    /// 该 Heading 下辖的 Blocks（直到下一个 HeadingBlock 或 EOF）。
    /// </summary>
    public IReadOnlyList<Block> Content { get; }

    /// <summary>
    /// 创建 ATX Section。
    /// </summary>
    /// <param name="heading">ATX Heading Block。</param>
    /// <param name="content">下辖的 Blocks。</param>
    public AtxSection(HeadingBlock heading, IReadOnlyList<Block> content) {
        Heading = heading;
        Content = content;
    }
}
