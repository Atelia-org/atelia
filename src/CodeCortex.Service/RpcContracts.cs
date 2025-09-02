namespace CodeCortex.Service;

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
