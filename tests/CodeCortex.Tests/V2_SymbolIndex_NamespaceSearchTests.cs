using System;
using System.Collections.Generic;
using System.Linq;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;
using CodeCortexV2.Index.SymbolTreeInternal;
using Xunit;

namespace CodeCortex.Tests;

/// <summary>
/// 这些测试从旧的 SymbolIndex 测试迁移而来，改为直接针对新的 SymbolTreeB。
/// 仅覆盖当前已实现能力：精确匹配、大小写不敏感、泛型元数不敏感（不包含 Partial/Wildcard/Fuzzy）。
/// </summary>
public class V2_SymbolIndex_NamespaceSearchTests {
    // 构造命名空间条目（与 V2_SymbolTree_SearchTests 中保持一致风格）
    private static SymbolEntry Ns(string name, string? parent = null) {
        var fqn = "global::" + name;
        var simple = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
        return new SymbolEntry(
            DocCommentId: "N:" + name,
            Assembly: string.Empty,
            Kind: SymbolKinds.Namespace,
            ParentNamespaceNoGlobal: parent ?? (name.Contains('.') ? name[..name.LastIndexOf('.')] : string.Empty),
            FqnNoGlobal: name,
            FqnLeaf: simple
        );
    }

    // 构造类型条目（与 V2_SymbolTree_SearchTests 一致）
    private static SymbolEntry Ty(string ns, string nameBase, int arity, string assembly) {
        string docId = arity > 0 ? $"T:{ns}.{nameBase}`{arity}" : $"T:{ns}.{nameBase}";
        string fqn = arity > 0
            ? $"global::{ns}.{nameBase}<T{(arity > 1 ? "," + string.Join(",", Enumerable.Range(2, arity - 1).Select(i => "T" + i)) : string.Empty)}>"
            : $"global::{ns}.{nameBase}";
        string fqnNoGlobal = IndexStringUtil.StripGlobal(fqn);
        string simple = nameBase;
        string parentNs = ns;
        return new SymbolEntry(
            DocCommentId: docId,
            Assembly: assembly,
            Kind: SymbolKinds.Type,
            ParentNamespaceNoGlobal: parentNs,
            FqnNoGlobal: fqnNoGlobal,
            FqnLeaf: simple
        );
    }

    private static SymbolTreeB BuildFooBarSample() {
        var entries = new List<SymbolEntry>
        {
            Ns("Foo"),
            Ns("Foo.Bar", "Foo"),
            Ty("Foo.Bar", "Baz", 0, "TestAsm")
        };
        return SymbolTreeB.FromEntries(entries);
    }

    /// <summary>
    /// DocId 精确查询命名空间（N:Foo.Bar）。
    /// 目的：验证命名空间命中、父命名空间字段、命名空间不带程序集（Assembly=null）。
    /// 约束：不涉及模糊/通配匹配，仅等值匹配。
    /// </summary>
    [Fact]
    public void Search_ByNamespaceDocId_ReturnsNamespace() {
        var idx = BuildFooBarSample();
        var page = idx.Search("N:Foo.Bar", limit: 10, offset: 0, kinds: SymbolKinds.Namespace);

        Assert.Equal(1, page.Total);
        var item = page.Items[0];
        Assert.Equal(SymbolKinds.Namespace, item.Kind);
        Assert.Equal("Foo.Bar", item.Name);
        Assert.Equal("Foo", item.Namespace); // parent namespace captured
        Assert.Null(item.Assembly); // namespace assembly is undefined/null
    }

    /// <summary>
    /// 以外部显示名（无 global:: 前缀）的命名空间字符串进行查询（Foo.Bar），并按 kinds=Namespace 过滤。
    /// 目的：确保只返回命名空间项，并能包含目标命名空间。
    /// 约束：不展开子树，不返回类型；不涉及模糊/通配匹配。
    /// </summary>
    [Fact]
    public void Search_ByNamespaceName_WithFilter_ReturnsOnlyNamespace() {
        var idx = BuildFooBarSample();
        var page = idx.Search("Foo.Bar", limit: 10, offset: 0, kinds: SymbolKinds.Namespace);

        Assert.True(page.Total >= 1);
        Assert.All(page.Items, it => Assert.Equal(SymbolKinds.Namespace, it.Kind));
        Assert.Contains(page.Items, it => it.Name == "Foo.Bar");
    }

    /// <summary>
    /// 类型后缀查询（"Baz"），期望返回 Foo.Bar.Baz 类型并携带命名空间信息。
    /// 目的：验证后缀段匹配可命中类型节点，且 Name 为无 global:: 的全名，Namespace 为父命名空间。
    /// 约束：当前实现未启用模糊/通配，属于常规匹配路径。
    /// </summary>
    [Fact]
    public void Search_TypeSuffix_StillWorks_AndHasNamespace() {
        var idx = BuildFooBarSample();
        var page = idx.Search("Baz", limit: 10, offset: 0, kinds: SymbolKinds.All);

        Assert.True(page.Total >= 1);
        var item = page.Items.First(i => i.Name.EndsWith("Baz", StringComparison.Ordinal));
        Assert.Equal(SymbolKinds.Type, item.Kind);
        Assert.Equal("Foo.Bar", item.Namespace);
        Assert.Equal("Foo.Bar.Baz", item.Name);
    }

    /// <summary>
    /// 类型名后缀查询 + 命名空间过滤（kinds=Namespace）。
    /// 目的：在未实现 Fuzzy/Partial/Wildcard 时，类型后缀不应以命名空间形式返回，因此应为空集。
    /// 约束：一旦未来开启模糊能力，此测试需重新审视预期（当前显式要求空）。
    /// </summary>
    [Fact]
    public void Search_TypeSuffix_WithNamespaceFilter_ReturnsEmpty() {
        var idx = BuildFooBarSample();
        var page = idx.Search("Baz", limit: 10, offset: 0, kinds: SymbolKinds.Namespace);

        // 由于未实现 Fuzzy/Partial/Wildcard，按命名空间筛选类型后缀应返回空集
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }
}

