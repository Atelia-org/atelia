namespace Atelia.SessionJournal.MemoPod;

public sealed record Memo {
    internal Memo(MemoId id, string exactText) {
        Id = MemoPodSyntax.RequireMemoId(id, nameof(id));
        ExactTextUtf8ByteCount = MemoPodSyntax.RequireMemoExactText(
            exactText,
            nameof(exactText)
        );
        ExactText = exactText;
    }

    public MemoId Id { get; }
    public string ExactText { get; }

    internal int ExactTextUtf8ByteCount { get; }
}
