namespace CodeCortexV2.Abstractions;

public sealed record SearchHit(
    string Name,
    SymbolKinds Kind,
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



// Unified outline model for Markdown/JSON dual pipeline

public sealed record SymbolMetadata(
    string Fqn,
    string DocId,
    string? Assembly,
    string? FilePath
);

public sealed record SymbolOutline(
    SymbolKinds Kind,
    string Name,
    string Signature,
    IReadOnlyList<CodeCortexV2.Formatting.Block> XmlDocBlocks,
    IReadOnlyList<SymbolOutline> Members,
    SymbolMetadata Metadata
);
