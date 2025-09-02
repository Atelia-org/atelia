using System.Collections.Immutable;

namespace CodeCortex.Core.Models;

public sealed record TypeRecord(
    string Id,
    string Fqn,
    string ProjectId,
    string Kind,
    ImmutableArray<string> Files,
    TypeHashes Hashes,
    int OutlineVersion,
    int DepthHint)
{
    public static TypeRecord Create(string id,string fqn,string projectId,string kind,IEnumerable<string> files,TypeHashes hashes,int outlineVersion,int depthHint)
        => new(id,fqn,projectId,kind,files.ToImmutableArray(),hashes,outlineVersion,depthHint);
}
