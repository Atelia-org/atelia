namespace Atelia.StateJournal;

/// <summary>
/// 文本容器中的一个内容块，携带稳定 blocklock ID。
/// </summary>
public readonly record struct TextBlock(uint Id, string Content);
