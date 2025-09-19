using CodeCortex.ServiceV2.Graph.Abstractions;

namespace CodeCortex.ServiceV2.Services;

public sealed class OutlineService {
    private readonly IGraphEngine _graph;

    public OutlineService(IGraphEngine graph) {
        _graph = graph;
    }

    // M1 signature: build or fetch outline markdown for a type NodeId
    public async Task<string> GetOutlineAsync(string typeNodeId, CancellationToken ct = default) {
        // paramsHash should reflect config/render options; placeholder for M1
        var paramsHash = "default";
        var record = await _graph.GetOrBuildAsync(typeNodeId, NodeKind.OutlineMarkdown, paramsHash, ct);
        // For M1 we assume OutputRef resolves to a file path; actual blob store TBD
        return record.OutputRef ?? string.Empty;
    }
}

