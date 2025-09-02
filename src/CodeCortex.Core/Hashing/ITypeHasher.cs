using Microsoft.CodeAnalysis;
using CodeCortex.Core.Models;

namespace CodeCortex.Core.Hashing;

public interface ITypeHasher
{
    TypeHashes Compute(INamedTypeSymbol symbol, IReadOnlyList<string> partialFilePaths, HashConfig config);
}

public sealed record HashConfig(
    bool IncludeInternalInStructureHash = false,
    bool StructureHashIncludesXmlDoc = false,
    bool SkipCosmeticRewrite = false);
