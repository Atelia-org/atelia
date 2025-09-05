using System.Threading.Tasks;
using Atelia.Analyzers.Style.Tests.TestHelpers;
using Xunit;

namespace Atelia.Analyzers.Style.Tests;

public class MT0101XmlDocEscapeTests {
    private const string AnalyzerType = "Atelia.Analyzers.Style.MT0101XmlDocEscapeAnalyzer";

    [Fact]
    public async Task CodeFix_Escapes_Generic_Angles_In_Summary() {
        var code = @"/// <summary>SlidingQueue<T> is a queue</summary>
class C { }";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.MT0101XmlDocEscapeCodeFix(), "MT0101");
        Assert.Contains("SlidingQueue&lt;T&gt;", fixedText);
    }

    [Fact]
    public async Task CodeFix_Escapes_LessEqual_In_Text() {
        var code = @"/// <summary>0 <= _head <= _items.Count</summary>
class C { }";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.MT0101XmlDocEscapeCodeFix(), "MT0101");
        Assert.Contains("0 &lt;= _head &lt;= _items.Count", fixedText);
    }
}
