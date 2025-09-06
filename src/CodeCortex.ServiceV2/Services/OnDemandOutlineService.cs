using CodeCortex.Core.Hashing;
using CodeCortex.Core.Models;
using CodeCortex.Core.Outline;
using CodeCortex.Workspace;
using Microsoft.CodeAnalysis;

namespace CodeCortex.ServiceV2.Services;

public interface IOnDemandOutlineService {
    Task<string?> GetOutlineByFqnAsync(string entryPath, string fullyQualifiedName, CancellationToken ct = default);
}

public sealed class OnDemandOutlineService : IOnDemandOutlineService {
    private readonly IWorkspaceLoader _loader;
    private readonly ITypeEnumerator _types;
    private readonly ITypeHasher _hasher;
    private readonly IOutlineExtractor _outline;

    public OnDemandOutlineService(
        IWorkspaceLoader? loader = null,
        ITypeEnumerator? types = null,
        ITypeHasher? hasher = null,
        IOutlineExtractor? outline = null
    ) {
        _loader = loader ?? new MsBuildWorkspaceLoader();
        _types = types ?? new RoslynTypeEnumerator();
        _hasher = hasher ?? new TypeHasher();
        _outline = outline ?? new OutlineExtractor();
    }

    public async Task<string?> GetOutlineByFqnAsync(string entryPath, string fullyQualifiedName, CancellationToken ct = default) {
        // Normalize inputs
        var targetFqn = NormalizeFqn(fullyQualifiedName);

        var loaded = await _loader.LoadAsync(entryPath, ct).ConfigureAwait(false);
        foreach (var project in loaded.Projects) {
            var comp = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (comp == null) {
                continue;
            }

            foreach (var t in _types.Enumerate(comp, ct)) {
                var fqn = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (NormalizeFqn(fqn) != targetFqn) {
                    continue;
                }

                // Compute hashes (no cache path)
                var hashes = _hasher.Compute(t, Array.Empty<string>(), new HashConfig());
                var md = _outline.BuildOutline(t, hashes, new OutlineOptions());
                return md;
            }
        }
        return null;
    }

    private static string NormalizeFqn(string fqn) {
        if (string.IsNullOrWhiteSpace(fqn)) {
            return string.Empty;
        }

        var s = fqn.Trim();
        // Roslyn FullyQualifiedFormat prefixes with global::
        if (!s.StartsWith("global::", StringComparison.Ordinal)) {
            s = "global::" + s;
        }
        return s;
    }
}

