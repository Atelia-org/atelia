namespace CodeCortex.ServiceV2.Graph.Abstractions;

public enum NodeKind {
    SourceFile,
    CompleteType,
    OutlineInfo,
    OutlineMarkdown,
}

public sealed record NodeInput(string NodeId, string Role, string? Selector, string InputHash);

public sealed record NodeRecord(
    string NodeId,
    NodeKind Kind,
    IReadOnlyList<NodeInput> Inputs,
    string ParamsHash,
    string Producer,
    string? OutputRef,
    string? OutputHash,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public interface IGraphEngine {
    // Returns OutputRef/OutputHash and records dependencies; computes on miss.
    Task<NodeRecord> GetOrBuildAsync(string nodeId, NodeKind kind, string paramsHash, CancellationToken ct = default);

    // Marks dependents as stale (future: store stale flag in metadata)
    Task InvalidateByInputAsync(string inputNodeId, CancellationToken ct = default);
}
