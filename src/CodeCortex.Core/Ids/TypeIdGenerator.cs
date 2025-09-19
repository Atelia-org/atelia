using System.Collections.Concurrent;
using System.Text;
using CodeCortex.Core.Util;
using Microsoft.CodeAnalysis;

namespace CodeCortex.Core.Ids;

/// <summary>
/// Generates stable TypeIds from Roslyn symbols using SHA256 + Base32 truncation with collision extension.
/// </summary>
public static class TypeIdGenerator {
    private static readonly ConcurrentDictionary<string, string> _fqnToId = new();
    private static readonly ConcurrentDictionary<string, string> _idToFqn = new();
    private static string? _logDir;

    /// <summary>Initialize logging root (must be called before first GetId to enable conflict logging).</summary>
    public static void Initialize(string rootDir) {
        var logDir = Path.Combine(rootDir, ".codecortex", "logs");
        Directory.CreateDirectory(logDir);
        _logDir = logDir;
    }

    /// <summary>Get or create a stable TypeId for the given symbol.</summary>
    public static string GetId(INamedTypeSymbol symbol) {
        var fqn = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return _fqnToId.GetOrAdd(fqn, _ => CreateId(symbol, fqn));
    }

    private static string CreateId(INamedTypeSymbol symbol, string fqn) {
        var baseInput = fqn + "|" + symbol.TypeKind + "|" + symbol.Arity;
        var id = "T_" + HashUtil.Sha256Base32Trunc(baseInput, 8);
        if (_idToFqn.TryAdd(id, fqn)) { return id; }
        id = "T_" + HashUtil.Sha256Base32Trunc(baseInput, 12);
        if (!_idToFqn.TryAdd(id, fqn)) {
            // extremely unlikely: log and append random suffix
            id = id + "_X";
        }
        LogConflict(fqn, id);
        return id;
    }

    private static void LogConflict(string fqn, string id) {
        try {
            if (_logDir == null) { return; }
            File.AppendAllText(Path.Combine(_logDir, "hash_conflicts.log"), $"[{DateTime.UtcNow:O}] TypeIdConflict id={id} fqn={fqn}\n");
        }
        catch { }
    }
}
