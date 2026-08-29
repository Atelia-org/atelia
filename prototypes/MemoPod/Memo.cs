namespace Atelia.MemoPod;

public sealed record Memo {
    internal Memo(
        MemoId id,
        string exactText,
        string? title = null,
        string? gist = null,
        string? summary = null
    ) {
        Id = MemoPodSyntax.RequireMemoId(id, nameof(id));
        ExactTextUtf8ByteCount = MemoPodSyntax.RequireMemoExactText(
            exactText,
            nameof(exactText)
        );
        TitleUtf8ByteCount = MemoPodSyntax.RequireOptionalMemoMetadata(
            title,
            "title",
            MemoPodLimits.MaximumMemoTitleUtf8Bytes,
            nameof(title)
        );
        GistUtf8ByteCount = MemoPodSyntax.RequireOptionalMemoMetadata(
            gist,
            "gist",
            MemoPodLimits.MaximumMemoGistUtf8Bytes,
            nameof(gist)
        );
        SummaryUtf8ByteCount = MemoPodSyntax.RequireOptionalMemoMetadata(
            summary,
            "summary",
            MemoPodLimits.MaximumMemoSummaryUtf8Bytes,
            nameof(summary)
        );
        ExactText = exactText;
        Title = title;
        Gist = gist;
        Summary = summary;
        MetadataUtf8ByteCount = checked(
            TitleUtf8ByteCount + GistUtf8ByteCount + SummaryUtf8ByteCount
        );
    }

    public MemoId Id { get; }
    public string? Title { get; }
    public string? Gist { get; }
    public string? Summary { get; }
    public string ExactText { get; }

    internal int ExactTextUtf8ByteCount { get; }
    internal int TitleUtf8ByteCount { get; }
    internal int GistUtf8ByteCount { get; }
    internal int SummaryUtf8ByteCount { get; }
    internal int MetadataUtf8ByteCount { get; }
}
