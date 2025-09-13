
namespace CodeCortexV2.Abstractions;
public interface ISymbolIndex {
    SearchResults Search(string query, int limit, int offset, SymbolKinds kinds);
    ISymbolIndex WithDelta(SymbolsDelta delta);
}
