using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortexV2.Abstractions;

namespace CodeCortexV2.Index.SymbolTreeInternal;

/// <summary>
/// 集中管理 Query / Index 构建两侧共享的最小归一化与解析逻辑。
/// 仅收敛纯函数，不引入新领域模型；保持向后兼容。
/// </summary>
internal static class SymbolNormalization {
    private const string GlobalRootPrefix = "global::";

    /// <summary>去掉前缀 global:: （区分大小写）。</summary>
    public static string StripGlobalPrefix(string text) =>
        text.StartsWith(GlobalRootPrefix, StringComparison.Ordinal)
            ? text.Substring(GlobalRootPrefix.Length)
            : text;

    /// <summary>
    /// 解析 DocumentationCommentId 风格的前缀（N:/T:/M:/P:/F:/E:）。
    /// 不支持 "!:"（直接返回 false）。
    /// 不会 Trim 输入，调用方需自处理。
    /// </summary>
    public static bool TryParseDocIdPrefix(string text, out SymbolKinds kind, out string remainder) {
        kind = SymbolKinds.None;
        remainder = text;
        if (string.IsNullOrEmpty(text) || text.Length < 2) { return false; }
        if (text[1] != ':') { return false; }
        char c = text[0];
        if (c == '!') { return false; /* 内部前缀：拒绝 */ }
        kind = c switch {
            'N' => SymbolKinds.Namespace,
            'T' => SymbolKinds.Type,
            'M' => SymbolKinds.Method,
            'P' => SymbolKinds.Property,
            'F' => SymbolKinds.Field,
            'E' => SymbolKinds.Event,
            _ => SymbolKinds.None
        };
        if (kind == SymbolKinds.None) { return false; }
        remainder = text.Substring(2);
        return true;
    }

    /// <summary>
    /// 检测并移除尾随 '.'（多个 '.' 视为一个语义）。返回是否存在尾随点。
    /// </summary>
    public static bool HasTrailingDirectChildMarker(string raw, out string withoutMarker) {
        if (string.IsNullOrEmpty(raw)) {
            withoutMarker = raw;
            return false;
        }
        int i = raw.Length - 1;
        while (i >= 0 && raw[i] == '.') { i--; }
        if (i == raw.Length - 1) {
            withoutMarker = raw;
            return false;
        }
        withoutMarker = raw.Substring(0, i + 1);
        return true;
    }

    /// <summary>
    /// 按 '.' 分段，并对最后一段按 '+' 展开（嵌套类型）。忽略空段（压缩连续的 '..').
    /// </summary>
    public static string[] SplitSegmentsWithNested(string q) {
        if (string.IsNullOrEmpty(q)) { return Array.Empty<string>(); }
        var parts = q.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { return parts; }
        var last = parts[^1];
        var plusSegs = last.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (plusSegs.Length == 1) { return parts; }
        var list = new List<string>(parts.Length - 1 + plusSegs.Length);
        for (int i = 0; i < parts.Length - 1; i++) { list.Add(parts[i]); }
        list.AddRange(plusSegs);
        return list.ToArray();
    }

    /// <summary>
    /// 解析泛型：支持 反引号形式 `n 以及 &lt;T,U&gt; / &lt;,&gt; / &lt;int,string&gt; 等。
    /// 返回: baseName(无反引号/泛型参数部分) + arity（参数个数；&lt;&gt; 视为0；&lt;,&gt; 视为逗号数+1）。
    /// hadExplicitGenericSyntax=true 表示使用了 &lt;...&gt; 或 反引号。
    /// </summary>
    public static (string baseName, int arity, bool hadExplicit) ParseGenericArity(string segment) {
        if (string.IsNullOrEmpty(segment)) { return (segment, 0, false); }
        string baseName = segment;
        int arity = 0;
        bool explicitGen = false;

        int back = segment.IndexOf('`');
        if (back >= 0) {
            baseName = segment.Substring(0, back);
            var numStr = new string(segment.Skip(back + 1).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(numStr, out var n1)) {
                arity = n1;
                explicitGen = true;
            }
        }

        int lt = segment.IndexOf('<');
        if (lt >= 0) {
            baseName = segment.Substring(0, lt);
            var inside = segment.Substring(lt + 1);
            int rt = inside.LastIndexOf('>');
            if (rt >= 0) {
                inside = inside.Substring(0, rt);
                explicitGen = true;
                if (inside.Length > 0) {
                    // 统计参数个数：逗号数量 + 1；全为空不增加
                    arity = inside.Count(c => c == ',') + 1;
                }
                else {
                    // &lt;&gt; => 0
                    if (arity == 0) { arity = 0; }
                }
            }
        }

        return (baseName, arity, explicitGen);
    }

    /// <summary>平衡性检测：&lt; 与 &gt; 匹配；遇到负值提早返回 false。</summary>
    public static bool IsBalancedGenericAngles(string segment) {
        int bal = 0;
        foreach (var ch in segment) {
            if (ch == '<') { bal++; }
            else if (ch == '>') { bal--; }
            if (bal < 0) { return false; }
        }
        return bal == 0;
    }

    /// <summary>若 ToLowerInvariant 与原值不同则返回 lower，否则返回 null。</summary>
    public static string? ToLowerIfDifferent(string s) {
        if (string.IsNullOrEmpty(s)) { return null; }
        var lower = s.ToLowerInvariant();
        return lower == s ? null : lower;
    }

    /// <summary>移除反引号泛型后缀：Foo`1 -&gt; Foo</summary>
    public static string RemoveGenericAritySuffix(string name) {
        if (string.IsNullOrEmpty(name)) { return name; }
        int idx = name.IndexOf('`');
        return idx >= 0 ? name.Substring(0, idx) : name;
    }

    /// <summary>从 FQN 提取命名空间链（不含最后的类型/叶名；输入不含 global::）。</summary>
    public static string[] BuildNamespaceChain(string fqnNoGlobal) {
        if (string.IsNullOrEmpty(fqnNoGlobal)) { return Array.Empty<string>(); }
        var parts = fqnNoGlobal.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) { return Array.Empty<string>(); }
        var arr = new string[parts.Length - 1];
        Array.Copy(parts, 0, arr, 0, arr.Length);
        return arr;
    }

    /// <summary>Outer+Inner+Sub -&gt; [Outer,Inner,Sub]</summary>
    public static string[] ExpandNestedDisplay(string lastSegmentWithPlus) {
        if (string.IsNullOrEmpty(lastSegmentWithPlus)) { return Array.Empty<string>(); }
        return lastSegmentWithPlus.Split('+', StringSplitOptions.RemoveEmptyEntries);
    }
}
