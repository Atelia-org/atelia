namespace CodeCortex.Core.Models;

public sealed record TypeHashes(
    string Structure,
    string PublicImpl,
    string InternalImpl,
    string XmlDoc,
    string Cosmetic,
    string Impl)
{
    public static readonly TypeHashes Empty = new("","","","","","");
}
