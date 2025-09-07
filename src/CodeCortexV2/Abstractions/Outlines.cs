namespace CodeCortexV2.Abstractions;

public sealed record SearchHit(
    string Name,
    SymbolKind Kind,
    string? Namespace,
    string? Assembly,
    SymbolId SymbolId,
    MatchKind MatchKind,
    bool IsAmbiguous,
    double Score
);

public sealed record SearchResults(
    IReadOnlyList<SearchHit> Items,
    int Total,
    int Offset,
    int Limit,
    int? NextOffset
);

public sealed record Location(string FilePath, int Start, int Length);

public sealed record MemberOutline(
    string Kind,
    string Name,
    string Signature,
    string? Summary,
    Location DeclaredIn
);

public sealed record TypeOutline(
    string Name,
    string? Summary,
    IReadOnlyList<MemberOutline> Members
);

public sealed record NamespaceOutline(
    string Name,
    string? Summary,
    IReadOnlyList<string> TypeNames
);

public sealed record AssemblyOutline(
    string Name,
    IReadOnlyList<string> Namespaces
);

public sealed record TypeSource(
    string Name,
    string FullMergedText,
    IReadOnlyList<TypeSourcePart> Parts
);

public sealed record TypeSourcePart(
    string FilePath,
    int Start,
    int Length
);

