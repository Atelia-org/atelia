using System;

namespace CodeCortexV2.Abstractions;

public readonly record struct SymbolId(string Value) {
    public override string ToString() => Value;
}

public interface ISymbolIndex {
    SearchResults Search(string query, int limit, int offset, SymbolKinds kinds);
}

[Flags]
public enum SymbolKinds {
    None = 0,
    Namespace = 1 << 0,
    Type = 1 << 1,
    Method = 1 << 2,
    Property = 1 << 3,
    Field = 1 << 4,
    Event = 1 << 5,
    Unknown = 1 << 6,
    All = Namespace | Type | Method | Property | Field | Event | Unknown
}
public enum MatchKind {
    Id = 0,
    Exact = 1,
    ExactIgnoreCase = 2,
    Prefix = 3,
    Contains = 4,
    Suffix = 5,
    Wildcard = 6,
    GenericBase = 7,
    Fuzzy = 8
}

// New unified providers returning SymbolOutline
public interface IMemberOutlineProvider {
    Task<SymbolOutline> GetMemberOutlineAsync(SymbolId memberId, OutlineOptions? options, CancellationToken ct);
}

public interface ITypeOutlineProvider {
    Task<SymbolOutline> GetTypeOutlineAsync(SymbolId typeId, OutlineOptions? options, CancellationToken ct);
}

public interface INamespaceOutlineProvider {
    Task<SymbolOutline> GetNamespaceOutlineAsync(SymbolId namespaceId, OutlineOptions? options, CancellationToken ct);
}

public interface IAssemblyOutlineProvider {
    Task<AssemblyOutline> GetAssemblyOutlineAsync(SymbolId assemblyId, OutlineOptions? options, CancellationToken ct);
}

public interface ITypeSourceProvider {
    Task<TypeSource> GetTypeSourceAsync(SymbolId typeId, SourceOptions? options, CancellationToken ct);
}

public sealed record OutlineOptions(bool Markdown = true, int? MaxItems = null);
public sealed record SourceOptions(bool Markdown = true, bool IncludeParts = true);

