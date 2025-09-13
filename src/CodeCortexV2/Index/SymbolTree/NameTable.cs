
using System.Collections.Immutable;

namespace CodeCortexV2.Index.SymbolTreeInternal;

/// <summary>
/// String table for names; supports aliasing by inserting multiple keys mapping to the same nameId.
/// </summary>
internal sealed class NameTable {
    private readonly ImmutableArray<string> _names; // id -> canonical name
    private readonly Dictionary<string, int> _map;  // alias -> id

    public NameTable(ImmutableArray<string> names, Dictionary<string, int> map) {
        _names = names;
        _map = map;
    }

    public int Count => _names.Length;
    public string this[int id] => _names[id];
    public bool TryGetId(string name, out int id) => _map.TryGetValue(name, out id);
    public IEnumerable<KeyValuePair<string, int>> Aliases => _map;

    public static NameTable Empty { get; } = new(
        ImmutableArray<string>.Empty,
        new Dictionary<string, int>(StringComparer.Ordinal)
    );
}
