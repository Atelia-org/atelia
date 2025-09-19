using System;

namespace CodeCortexV2.Abstractions;

// Alias relation flags for how an alias relates to a node.
[Flags]
public enum MatchFlags {
    None = 0,
    IgnoreGenericArity = 1 << 0,
    IgnoreCase = 1 << 1, // MatchCase的反面
    Partial = 1 << 2, // MatchWholeWord的反面
    Wildcard = 1 << 3, // 支持`*`,`?`
    Fuzzy = 1 << 4, // 基于最短编辑举例
}
