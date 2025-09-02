using System.Collections.Generic;

namespace CodeCortex.Core.Hashing;

/// <summary>
/// Intermediate collected artifacts before hashing for a type.
/// </summary>
public sealed record HasherIntermediate(
    IReadOnlyList<string> StructureLines,
    IReadOnlyList<string> PublicBodies,
    IReadOnlyList<string> InternalBodies,
    string XmlFirstLine,
    string CosmeticRaw
);
