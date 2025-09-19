namespace CodeCortexV2.Formatting;

// Unified renderer-agnostic block model (aligns with design doc §1)
public abstract record Block;
public sealed record ParagraphBlock(string Text) : Block;
public sealed record CodeBlock(string Text, string? Language = null) : Block;
public sealed record SequenceBlock(List<Block> Children) : Block;
public sealed record ListBlock(bool Ordered, List<SequenceBlock> Items) : Block;
public sealed record TableBlock(List<ParagraphBlock> Headers, List<List<ParagraphBlock>> Rows) : Block;
public sealed record SectionBlock(string Heading, SequenceBlock Body) : Block;
