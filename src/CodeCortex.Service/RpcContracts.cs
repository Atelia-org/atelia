
namespace CodeCortex.Service;

public enum MatchKind { Exact = 0, ExactIgnoreCase = 1, Suffix = 2, Wildcard = 3, Fuzzy = 4 }

public sealed record SymbolMatch(string Id, string Fqn, string Kind, MatchKind MatchKind, int RankScore, int? Distance, bool IsAmbiguous = false);

public static class RpcMethods {
    public const string ResolveSymbol = "resolveSymbol";
    public const string GetOutline = "getOutline";
    public const string SearchSymbols = "searchSymbols";
    public const string Status = "status";
}

public sealed record ResolveRequest(string Query);
public sealed record OutlineRequest(string QueryOrId);
public sealed record SearchRequest(string Query, int Limit = 20);
public sealed record StatusResponse(int Projects, int TypesIndexed, long InitialIndexDurationMs, long LastIncrementalMs, int WatcherQueueDepth, double OutlineCacheHitRatio, long MemoryMB);
