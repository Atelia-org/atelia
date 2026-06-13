namespace Atelia.TextEditScript;

public abstract record TextEditOperation;

public sealed record InsertTextEdit(
    TextInsertSide Side,
    TextAnchor Anchor,
    string Content) : TextEditOperation;

public sealed record ReplaceTextEdit(
    TextAnchor Anchor,
    string Content) : TextEditOperation;

public sealed record DeleteTextEdit(
    TextAnchor Anchor) : TextEditOperation;

public sealed record SplitTextEdit(
    TextAnchor Anchor,
    int Offset) : TextEditOperation;

public sealed record MergeTextEdit(
    TextAnchor Anchor) : TextEditOperation;
