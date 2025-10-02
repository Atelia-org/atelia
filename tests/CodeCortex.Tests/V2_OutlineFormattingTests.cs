using Xunit;
using Atelia.CodeCortex.Tests.Util;
using Microsoft.CodeAnalysis;
using CodeCortexV2.Formatting;

namespace Atelia.CodeCortex.Tests;

public class V2_OutlineFormattingTests {
    private const string Source = @"namespace N {
    /// <summary>Demo type.</summary>
    public class C {
        /// <summary>Combine two values into a tuple.</summary>
        /// <typeparam name=""TKey"">Key type.</typeparam>
        /// <typeparam name=""TValue"">Value type.</typeparam>
        /// <param name=""key"">The <see cref=""TKey""/> key.</param>
        /// <param name=""value"">The <see cref=""TValue""/> value.</param>
        /// <returns>A tuple of ([key], [value]).</returns>
        /// <exception cref=""System.ArgumentNullException"">If <paramref name=""key""/> is null for reference types.</exception>
        public (TKey, TValue) Combine<TKey, TValue>(TKey key, TValue value) => (key, value);

        /// <summary>Table showcase</summary>
        /// <param name=""count"">Item count.</param>
        /// <returns>
        /// <list type=""table""><listheader><term>State</term><term>Meaning</term></listheader>
        /// <item><term>Empty</term><term>No items</term></item>
        /// <item><term>NonEmpty</term><term>Has items</term></item>
        /// </list>
        /// </returns>
        /// <exception cref=""System.ArgumentException"">
        /// <list type=""table""><listheader><term>Param</term><term>Rule</term></listheader>
        /// <item><term>count</term><term>Must be &gt;= 0</term></item>
        /// </list>
        /// </exception>
        public string TableShowcase(int count) => string.Empty;
    }
}";

    private static IMethodSymbol GetMethod(string typeName, string methodName) {
        var (_, type) = RoslynTestHost.CreateSingleType(Source, typeName);
        var m = type.GetMembers().OfType<IMethodSymbol>().First(x => x.Name == methodName);
        return m;
    }

    [Fact]
    public void SectionHeadings_NoBlankLineBeforeContent() {
        var m = GetMethod("N.C", "Combine");
        var blocks = XmlDocFormatter.BuildMemberBlocks(m);
        var md = MarkdownLayout.RenderBlocksToMarkdown(blocks, indent: "");
        var norm = md.Replace("\r\n", "\n");
        Atelia.Diagnostics.DebugUtil.Print("V2OutlineTest", "Combine()\n" + norm);
        Assert.DoesNotContain("Type Parameters:\n\n", norm);
        Assert.DoesNotContain("Parameters:\n\n", norm);
        Assert.DoesNotContain("Returns:\n\n", norm);
        Assert.DoesNotContain("Exceptions:\n\n", norm);
    }

    [Fact]
    public void SectionHeading_WithTable_SticksToTable() {
        var m = GetMethod("N.C", "TableShowcase");
        var blocks = XmlDocFormatter.BuildMemberBlocks(m);
        var md = MarkdownLayout.RenderBlocksToMarkdown(blocks, indent: "");
        var norm = md.Replace("\r\n", "\n");
        Atelia.Diagnostics.DebugUtil.Print("V2OutlineTest", "TableShowcase()\n" + norm);
        // Parameters rendered (Section-of-Sections in V2)
        Assert.Contains("Parameters", norm);
        Assert.Contains("count", norm);
        Assert.Contains("Item count.", norm);
        // Ensure no triple blank lines after headings (robust against presence/absence of returns/exceptions)
        Assert.DoesNotContain("Parameters\n\n\n", norm);
        Assert.DoesNotContain("Returns\n\n\n|", norm);
        Assert.DoesNotContain("Exceptions\n\n\n|", norm);
    }
}

