using System;

namespace CodeCortex.Workspace.SymbolQuery {
    public sealed record NameIndex(
        string Version,
        string SolutionPath,
        long BuiltAtUnixMs,
        int ProjectCount,
        int TypeCount,
        string[] Fqns
    );
}

