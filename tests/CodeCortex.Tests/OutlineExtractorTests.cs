using Xunit;
using CodeCortex.Core.Hashing;
using CodeCortex.Core.Outline;
using CodeCortex.Core.Models;
using CodeCortex.Core.Ids;
using CodeCortex.Tests.Util;

namespace CodeCortex.Tests;

public class OutlineExtractorTests {
    private const string Source = @"namespace N {
/// <summary>Calc</summary>
public class C {
    public int Add(int a,int b)=>a+b;
    protected int P => 1;
    internal int Hidden()=>0;
}
}";

    [Fact]
    public void Outline_Includes_PublicAndProtected_NotInternal() {
        TypeIdGenerator.Initialize(System.IO.Directory.GetCurrentDirectory());
        var (_, type) = RoslynTestHost.CreateSingleType(Source, "N.C");
        var hasher = new TypeHasher(new FakeHash(), new PassTrivia());
        var hashes = hasher.Compute(type, System.Array.Empty<string>(), new HashConfig());
        var extractor = new OutlineExtractor();
        var text = extractor.BuildOutline(type, hashes, new OutlineOptions());
        Atelia.Diagnostics.DebugUtil.Print("Outline", text);
        Assert.Contains("Add(int a, int b)", text);
        Assert.Contains("P { get;", text); // property accessor signature (allow formatting variance)
        Assert.DoesNotContain("Hidden", text);
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
