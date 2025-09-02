using Microsoft.CodeAnalysis;
using CodeCortex.Core.Models;

namespace CodeCortex.Core.Outline;

public interface IOutlineExtractor
{
    string BuildOutline(INamedTypeSymbol symbol, TypeHashes hashes, OutlineOptions options);
}

public sealed record OutlineOptions(bool IncludeXmlDocFirstLine = true);
