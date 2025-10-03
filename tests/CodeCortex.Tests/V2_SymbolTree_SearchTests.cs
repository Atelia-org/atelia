using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace Atelia.CodeCortex.Tests;

public class V2_SymbolTree_SearchTests {
    private static string[] BuildNamespaceSegments(string ns)
        => string.IsNullOrEmpty(ns)
            ? Array.Empty<string>()
            : ns.Split('.', StringSplitOptions.RemoveEmptyEntries);

    private static string[] BuildTypeSegments(string docId, int namespaceSegmentCount) {
        var body = docId.Substring(2);
        var all = SymbolNormalization.SplitSegmentsWithNested(body);
        if (namespaceSegmentCount == 0) { return all; }
        return all.Skip(namespaceSegmentCount).ToArray();
    }

    private static SymbolEntry Ty(string ns, string nameBase, int arity, string assembly) {
        // Build values similar to Roslyn projection
        string docId = arity > 0
            ? $"T:{ns}.{nameBase}`{arity}"
            : $"T:{ns}.{nameBase}";
        string fqn = arity > 0
            ? $"global::{ns}.{nameBase}<T{(arity > 1 ? "," + string.Join(",", Enumerable.Range(2, arity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : $"global::{ns}.{nameBase}";
        string fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
        string simple = nameBase; // Roslyn's INamedTypeSymbol.Name returns base without `n
        var namespaceSegments = BuildNamespaceSegments(ns);
        return new SymbolEntry(
            DocCommentId: docId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            NamespaceSegments: namespaceSegments,
            TypeSegments: BuildTypeSegments(docId, namespaceSegments.Length),
            FullDisplayName: fqnNoGlobal,
            DisplayName: simple
        );
    }

    private static SymbolTree BuildSample() {
        var entries = new[] {
            Ty("System.Collections.Generic", "List", 1, "System.Collections")
        };
        return BuildTree(entries);
    }

    private static SymbolEntry TyNested(string ns, string outerBase, int outerArity, string innerBase, int innerArity, string assembly) {
        // DocId: T:Ns.Outer`n+Inner`m (we'll use innerArity=0 for these tests)
        string docId = outerArity > 0
            ? $"T:{ns}.{outerBase}`{outerArity}+{innerBase}{(innerArity > 0 ? $"`{innerArity}" : string.Empty)}"
            : $"T:{ns}.{outerBase}+{innerBase}{(innerArity > 0 ? $"`{innerArity}" : string.Empty)}";

        // FQN: global::Ns.Outer<T,...>.Inner<...>
        string outerGenericParams = outerArity > 0
            ? $"<T{(outerArity > 1 ? "," + string.Join(",", Enumerable.Range(2, outerArity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : string.Empty;
        string innerGenericParams = innerArity > 0
            ? $"<T{(innerArity > 1 ? "," + string.Join(",", Enumerable.Range(2, innerArity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : string.Empty;
        string fqn = $"global::{ns}.{outerBase}{outerGenericParams}.{innerBase}{innerGenericParams}";
        string fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
        string simple = innerBase;
        var namespaceSegments = BuildNamespaceSegments(ns);
        return new SymbolEntry(
            DocCommentId: docId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            NamespaceSegments: namespaceSegments,
            TypeSegments: BuildTypeSegments(docId, namespaceSegments.Length),
            FullDisplayName: fqnNoGlobal,
            DisplayName: simple
        );
    }

    private static SymbolTree BuildSampleWithNested() {
        var entries = new[] {
            Ty("System.Collections.Generic", "List", 1, "System.Collections"),
            TyNested("System.Collections.Generic", "List", 1, "Enumerator", 0, "System.Collections")
        };
        return BuildTree(entries);
    }

    /// <summary>
    /// DocId 精确查询类型：等值匹配不打降级标志（MatchFlags.None），验证命中类型、命名空间、程序集。
    /// </summary>
    [Fact]
    public void DocId_Exact_Type() {
        var tree = BuildSample();
        var page = tree.Search("T:System.Collections.Generic.List`1", 10, 0, SymbolKinds.All);
        Assert.Equal(1, page.Total);
        var hit = Assert.Single(page.Items);
        Assert.Equal(SymbolKinds.Type, hit.Kind);
        // 新实现中“精确等值”匹配不设置任何降级标志，因此应为 None
        Assert.Equal(MatchFlags.None, hit.MatchFlags);
        Assert.Equal("System.Collections.Generic", hit.Namespace);
        Assert.Equal("System.Collections", hit.Assembly);
    }

    /// <summary>
    /// 使用 global:: 与泛型伪语法（&lt;T&gt;）的等值匹配，不涉及大小写或元数降级标志。
    /// </summary>
    [Fact]
    public void Exact_WithGlobalPrefix_And_GenericPseudo() {
        var tree = BuildSample();
        var page = tree.Search("global::System.Collections.Generic.List<T>", 10, 0, SymbolKinds.All);
        Assert.True(page.Total >= 1);
        Assert.Contains(page.Items, h => h.Kind == SymbolKinds.Type && h.MatchFlags == MatchFlags.None);
    }

    /// <summary>
    /// 命名空间命中不自动展开子树（不收集其子命名空间/类型），仅返回等值命中项。
    /// </summary>
    [Fact]
    public void Namespace_Exact_NoSubtreeExpansion() {
        var tree = BuildSample();
        var page = tree.Search("System.Collections", 100, 0, SymbolKinds.All);
        // 新语义：命名空间命中不自动展开子树
        Assert.Equal(1, page.Total);
        Assert.Contains(page.Items, h => h.Kind == SymbolKinds.Namespace && h.Name == "System.Collections");
        Assert.DoesNotContain(page.Items, h => h.Kind == SymbolKinds.Type && h.Name.StartsWith("System.Collections.Generic.List", StringComparison.Ordinal));
    }

    /// <summary>
    /// 查询泛型类型的基名（不带 `n 或 &lt;...&gt;）应以“元数不敏感等值”命中具体泛型节点，打上 IgnoreGenericArity。
    /// </summary>
    [Fact]
    public void Generic_BaseName_ArityInsensitive() {
        var tree = BuildSample();
        // 对于泛型类型，查询基名“List”应匹配到 List`1，但以 IgnoreGenericArity 标记
        var page = tree.Search("System.Collections.Generic.List", 10, 0, SymbolKinds.All);
        Assert.True(page.Total >= 1);
        Assert.Contains(page.Items, h => h.Kind == SymbolKinds.Type && h.Name == "System.Collections.Generic.List<T>" && (h.MatchFlags & MatchFlags.IgnoreGenericArity) != 0);
    }

    /// <summary>
    /// DocId 小写查询：等值逻辑匹配，但因大小写不一致需打上 IgnoreCase；其余语义与 DocId 精确一致。
    /// </summary>
    [Fact]
    public void CaseInsensitive_DocId_Generic_LastSegment() {
        var tree = BuildSample();
        var page = tree.Search("T:system.collections.generic.list`1", 10, 0, SymbolKinds.All);
        Assert.Equal(1, page.Total);
        var hit = Assert.Single(page.Items);
        Assert.Equal(SymbolKinds.Type, hit.Kind);
        Assert.Equal("System.Collections.Generic", hit.Namespace);
        Assert.Equal("System.Collections", hit.Assembly);
        Assert.True((hit.MatchFlags & MatchFlags.IgnoreCase) != 0);
    }

    /// <summary>
    /// global:: + 泛型伪语法的大小写不敏感匹配：命中同一类型并打上 IgnoreCase。
    /// </summary>
    [Fact]
    public void CaseInsensitive_GlobalPseudo_GenericPseudo() {
        var tree = BuildSample();
        var page = tree.Search("global::system.collections.generic.list<t>", 10, 0, SymbolKinds.All);
        Assert.True(page.Total >= 1);
        Assert.Contains(page.Items, h => h.Kind == SymbolKinds.Type && h.Name == "System.Collections.Generic.List<T>" && (h.MatchFlags & MatchFlags.IgnoreCase) != 0);
    }

    /// <summary>
    /// 命名空间查询大小写不敏感：命中目标命名空间并打上 IgnoreCase。
    /// </summary>
    [Fact]
    public void CaseInsensitive_Namespace() {
        var tree = BuildSample();
        var page = tree.Search("system.collections", 10, 0, SymbolKinds.All);
        Assert.Equal(1, page.Total);
        var hit = Assert.Single(page.Items);
        Assert.Equal(SymbolKinds.Namespace, hit.Kind);
        Assert.Equal("System.Collections", hit.Name);
        Assert.True((hit.MatchFlags & MatchFlags.IgnoreCase) != 0);
    }

    /// <summary>
    /// Root 约束 + 单段：仅允许顶层命名空间命中（父节点直接为根），用于约束结果范围。
    /// </summary>
    [Fact]
    public void RootAnchored_SingleSegment_OnlyTopNamespaces() {
        var tree = BuildSample();
        var page = tree.Search("global::System", 10, 0, SymbolKinds.All);
        Assert.Equal(1, page.Total);
        var hit = Assert.Single(page.Items);
        Assert.Equal(SymbolKinds.Namespace, hit.Kind);
        Assert.Equal("System", hit.Name);
    }

    /// <summary>
    /// Root 约束 + 单段：非顶层段名（不在根下）应被拒绝，不返回结果。
    /// </summary>
    [Fact]
    public void RootAnchored_SingleSegment_Reject_NonTop() {
        var tree = BuildSample();
        var page = tree.Search("global::Collections", 10, 0, SymbolKinds.All);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    /// <summary>
    /// Root 约束 + 多段：按分段逐级匹配，合法路径可被接受（最终命中对应命名空间）。
    /// </summary>
    [Fact]
    public void RootAnchored_MultiSegment_Accepts() {
        var tree = BuildSample();
        var page = tree.Search("global::System.Collections.Generic", 10, 0, SymbolKinds.All);
        Assert.Equal(1, page.Total);
        var hit = Assert.Single(page.Items);
        Assert.Equal(SymbolKinds.Namespace, hit.Kind);
        Assert.Equal("System.Collections.Generic", hit.Name);
    }

    /// <summary>
    /// 嵌套类型 DocId 精确匹配：使用 "+" 分段，等值命中不打任何降级标志。
    /// </summary>
    [Fact]
    public void NestedType_DocId_Exact() {
        var tree = BuildSampleWithNested();
        var page = tree.Search("T:System.Collections.Generic.List`1+Enumerator", 10, 0, SymbolKinds.All);
        Assert.Equal(1, page.Total);
        var hit = Assert.Single(page.Items);
        Assert.Equal(SymbolKinds.Type, hit.Kind);
        Assert.Equal("System.Collections.Generic.List<T>.Enumerator", hit.Name);
        Assert.Equal(MatchFlags.None, hit.MatchFlags);
    }

    /// <summary>
    /// 嵌套类型全名（global:: + &lt;T&gt; + .Inner）精确匹配：等值命中不打降级标志。
    /// </summary>
    [Fact]
    public void NestedType_GlobalPseudo_Exact() {
        var tree = BuildSampleWithNested();
        var page = tree.Search("global::System.Collections.Generic.List<T>.Enumerator", 10, 0, SymbolKinds.All);
        Assert.Equal(1, page.Total);
        var hit = Assert.Single(page.Items);
        Assert.Equal(SymbolKinds.Type, hit.Kind);
        Assert.Equal("System.Collections.Generic.List<T>.Enumerator", hit.Name);
        Assert.Equal(MatchFlags.None, hit.MatchFlags);
    }

    /// <summary>
    /// 路径使用点号连接嵌套类型，外层泛型允许“元数不敏感等值”匹配（IgnoreGenericArity）。
    /// </summary>
    [Fact]
    public void NestedType_PathWithDot_ArityInsensitive_ForOuter() {
        var tree = BuildSampleWithNested();
        var page = tree.Search("System.Collections.Generic.List.Enumerator", 10, 0, SymbolKinds.All);
        Assert.Equal(1, page.Total);
        var hit = Assert.Single(page.Items);
        Assert.Equal(SymbolKinds.Type, hit.Kind);
        Assert.Equal("System.Collections.Generic.List<T>.Enumerator", hit.Name);
        Assert.True((hit.MatchFlags & MatchFlags.IgnoreGenericArity) != 0);
    }

    private static SymbolTree BuildTree(IEnumerable<SymbolEntry> entries)
        => (SymbolTree)SymbolTree.Empty.WithDelta(
            SymbolsDeltaContract.Normalize(entries?.ToArray() ?? Array.Empty<SymbolEntry>(), Array.Empty<TypeKey>())
        );
}

