using Xunit;
using CodeCortex.Core.Hashing;
using CodeCortex.Core.Outline;
using CodeCortex.Core.Models;
using CodeCortex.Core.Ids;
using CodeCortex.Tests.Util;

namespace CodeCortex.Tests;

public class MarkdownRendererAndExtractorTests {
    private const string Source = @"namespace N {
    /// <summary>
    /// Demo summary.
    /// <list type=""number"">
    ///   <item><description>Step 1</description></item>
    ///   <item><description>Step 2</description></item>
    /// </list>
    /// <list type=""table"">
    ///   <listheader><term>Key</term><term>Value</term></listheader>
    ///   <item><term>A</term><term>Alpha</term></item>
    ///   <item><term>B</term><term>Beta</term></item>
    /// </list>
    /// </summary>
    public class C {
        /// <summary>Method with table in exceptions.</summary>
        /// <exception cref=""System.ArgumentException"">
        /// <list type=""table""><listheader><term>Param</term><term>Rule</term></listheader><item><term>x</term><term>Must be &gt; 0</term></item></list>
        /// </exception>
        public void M(int x) {}
    }
}";

    [Fact]
    public void Summary_NumberedList_UsesExplicitIndices() {
        TypeIdGenerator.Initialize(System.IO.Directory.GetCurrentDirectory());
        var (_, type) = RoslynTestHost.CreateSingleType(Source, "N.C");
        var hasher = new TypeHasher(new FakeHash(), new PassTrivia());
        var hashes = hasher.Compute(type, System.Array.Empty<string>(), new HashConfig());
        var extractor = new OutlineExtractor();
        var text = extractor.BuildOutline(type, hashes, new OutlineOptions());
        Atelia.Diagnostics.DebugUtil.Print("MyOutlineSummaryTest", text);
        Assert.Contains("XMLDOC:", text);
        Assert.Contains("1. Step 1", text);
        Assert.Contains("2. Step 2", text);
    }

    [Fact]
    public void Exceptions_FirstStructural_NoEmDash() {
        TypeIdGenerator.Initialize(System.IO.Directory.GetCurrentDirectory());
        var (_, type) = RoslynTestHost.CreateSingleType(Source, "N.C");
        var hasher = new TypeHasher(new FakeHash(), new PassTrivia());
        var hashes = hasher.Compute(type, System.Array.Empty<string>(), new HashConfig());
        var extractor = new OutlineExtractor();
        var text = extractor.BuildOutline(type, hashes, new OutlineOptions());
        Atelia.Diagnostics.DebugUtil.Print("MyOutlineExceptionsTest", text);
        // Should not join em dash with table header on first structural line
        Assert.DoesNotContain("ArgumentException — |", text);
        Assert.Contains("System.ArgumentException", text);
        Assert.Contains("|---|---|", text);
    }

    [Fact]
    public void TableBlankLineToggle_Works() {
        TypeIdGenerator.Initialize(System.IO.Directory.GetCurrentDirectory());
        var (_, type) = RoslynTestHost.CreateSingleType(Source, "N.C");
        var hasher = new TypeHasher(new FakeHash(), new PassTrivia());
        var hashes = hasher.Compute(type, System.Array.Empty<string>(), new HashConfig());
        var extractor = new OutlineExtractor();
        var text = extractor.BuildOutline(type, hashes, new OutlineOptions(IncludeXmlDocFirstLine: true));
        var norm = text.Replace("\r\n", "\n");
        Assert.Contains("XMLDOC:", norm);
        Assert.Contains("  2. Step 2\n\n  | Key | Value |", norm);
    }



    [Fact]
    public void NestedNumberedList_RendersWithExplicitIndices() {
        TypeIdGenerator.Initialize(System.IO.Directory.GetCurrentDirectory());
        var src = @"namespace N {
        /// <summary>
        /// <list type=""number"">
        ///   <item><description>Parent A</description>
        ///     <list type=""number"">
        ///       <item><description>Child 1</description></item>
        ///       <item><description>Child 2</description></item>
        ///     </list>
        ///   </item>
        /// </list>
        /// </summary>
        public class C { }
        }";
        var (_, type) = RoslynTestHost.CreateSingleType(src, "N.C");
        var hasher = new TypeHasher(new FakeHash(), new PassTrivia());
        var hashes = hasher.Compute(type, System.Array.Empty<string>(), new HashConfig());
        var extractor = new OutlineExtractor();
        var text = extractor.BuildOutline(type, hashes, new OutlineOptions());
        var norm = text.Replace("\r\n", "\n");
        Assert.Contains("XMLDOC:", norm);
        Assert.Contains("  1. Parent A", norm);
        Assert.Contains("1. Child 1", norm);
        Assert.Contains("2. Child 2", norm);
    }

    [Fact]
    public void ListItem_TermDesc_Combinations() {
        TypeIdGenerator.Initialize(System.IO.Directory.GetCurrentDirectory());
        var src = @"namespace N {
        /// <summary>
        /// <list type=""bullet"">
        ///   <item><term>TermOnly</term></item>
        ///   <item><description>DescOnly</description></item>
        ///   <item><term>T</term><description>D</description></item>
        /// </list>
        /// </summary>
        public class C { }
        }";
        var (_, type) = RoslynTestHost.CreateSingleType(src, "N.C");
        var hasher = new TypeHasher(new FakeHash(), new PassTrivia());
        var hashes = hasher.Compute(type, System.Array.Empty<string>(), new HashConfig());
        var extractor = new OutlineExtractor();
        var text = extractor.BuildOutline(type, hashes, new OutlineOptions());
        var norm = text.Replace("\r\n", "\n");
        Assert.Contains("XMLDOC:", norm);
        Assert.Contains("  - TermOnly", norm);
        Assert.Contains("  - DescOnly", norm);
        Assert.Contains("  - T — D", norm);
    }


    [Fact]
    public void InlineSeeInBullet_Renders() {
        TypeIdGenerator.Initialize(System.IO.Directory.GetCurrentDirectory());
        var src = @"namespace N {
        /// <summary>
        /// <list type=""bullet"">
        ///   <item>Bullet item B with <see cref=""string""/> and <c>inline code</c></item>
        /// </list>
        /// </summary>
        public class C { }
        }";
        var (_, type) = RoslynTestHost.CreateSingleType(src, "N.C");
        var hasher = new TypeHasher(new FakeHash(), new PassTrivia());
        var hashes = hasher.Compute(type, System.Array.Empty<string>(), new HashConfig());
        var extractor = new OutlineExtractor();
        var text = extractor.BuildOutline(type, hashes, new OutlineOptions());
        var norm = text.Replace("\r\n", "\n");
        Atelia.Diagnostics.DebugUtil.Print("InlineSeeTest", norm);
        Assert.Contains("  - Bullet item B with [string] and `inline code`", norm);
    }

    private sealed class FakeHash : IHashFunction {
        public string Compute(string input, int length = 8) {
            unchecked {
                int h = 23;
                foreach (var c in input) {
                    h = h * 37 + c;
                }

                h = System.Math.Abs(h);
                var s = h.ToString("X");
                return s.Length > length ? s.Substring(0, length) : s.PadLeft(length, '0');
            }
        }
    }
    private sealed class PassTrivia : ITriviaStripper { public string Strip(string c) => c; }
}
