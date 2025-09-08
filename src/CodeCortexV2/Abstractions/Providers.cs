namespace CodeCortexV2.Abstractions;

public readonly record struct SymbolId(string Value) {
    public override string ToString() => Value;
}

public interface ISymbolIndex {
    Task<SearchResults> SearchAsync(string query, SymbolKind? kindFilter, int limit, int offset, CancellationToken ct);
    Task<SymbolId?> ResolveAsync(string identifierOrName, CancellationToken ct);
}

public enum SymbolKind { Namespace, Type, Method, Property, Field, Event, Unknown }
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

public interface IMemberOutlineProvider {
    Task<MemberOutline> GetMemberOutlineAsync(SymbolId memberId, OutlineOptions? options, CancellationToken ct);
}

public interface ITypeOutlineProvider {
    Task<TypeOutline> GetTypeOutlineAsync(SymbolId typeId, OutlineOptions? options, CancellationToken ct);
}

public interface INamespaceOutlineProvider {
    Task<NamespaceOutline> GetNamespaceOutlineAsync(SymbolId namespaceId, OutlineOptions? options, CancellationToken ct);
}

public interface IAssemblyOutlineProvider {
    Task<AssemblyOutline> GetAssemblyOutlineAsync(SymbolId assemblyId, OutlineOptions? options, CancellationToken ct);
}

public interface ITypeSourceProvider {
    Task<TypeSource> GetTypeSourceAsync(SymbolId typeId, SourceOptions? options, CancellationToken ct);
}

public sealed record OutlineOptions(bool Markdown = true, int? MaxItems = null);
public sealed record SourceOptions(bool Markdown = true, bool IncludeParts = true);

