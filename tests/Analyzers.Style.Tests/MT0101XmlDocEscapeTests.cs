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

    [Fact]
    public async Task Keeps_ParamRef_And_TypeParamRef_Tags() {
        var code = @"/// <summary>See <paramref name=""x""/> and <typeparamref name=""T""/>.</summary>
class C<T> { void M(T x) {} }";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.MT0101XmlDocEscapeCodeFix(), "MT0101");
        Assert.Contains("<paramref name=\"x\"/>", fixedText);
        Assert.Contains("<typeparamref name=\"T\"/>", fixedText);
    }

    [Fact]
    public async Task Keeps_Inheritdoc_Tag() {
        var code = @"/// <inheritdoc/>
class D : System.IO.Stream { public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => 0; public override long Position { get => 0; set {} } public override void Flush() {} public override int Read(byte[] buffer, int offset, int count) => 0; public override long Seek(long offset, System.IO.SeekOrigin origin) => 0; public override void SetLength(long value) {} public override void Write(byte[] buffer, int offset, int count) {} }";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.MT0101XmlDocEscapeCodeFix(), "MT0101");
        Assert.Contains("<inheritdoc/>", fixedText);
    }

    [Fact]
    public async Task Keeps_Listheader_Within_List() {
        var code = @"/// <summary><list type=""table""><listheader><term>Col</term></listheader><item><term>V</term><description>Desc</description></item></list></summary>
class C { }";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.MT0101XmlDocEscapeCodeFix(), "MT0101");
        Assert.Contains("<listheader>", fixedText);
        Assert.Contains("</listheader>", fixedText);
    }

    [Fact]
    public async Task Keeps_WellFormed_Strong_Tag() {
        var code = @"/// <summary>Use <strong>bold</strong> text.</summary>
class C { }";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.MT0101XmlDocEscapeCodeFix(), "MT0101");
        Assert.Contains("<strong>bold</strong>", fixedText);
    }

    [Fact]
    public async Task Keeps_Remark_Tag_With_Generic_Text() {
        var code = @"/// <remark>Use List<int> for items.</remark>
class C { }";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.MT0101XmlDocEscapeCodeFix(), "MT0101");
        Assert.Contains("<remark>", fixedText);
        Assert.Contains("</remark>", fixedText);
        Assert.Contains("List&lt;int&gt;", fixedText);
    }

    [Fact]
    public async Task Escapes_Unmatched_Tag() {
        var code = @"/// <summary>Use <strong>bold text.</summary>
class C { }";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.MT0101XmlDocEscapeCodeFix(), "MT0101");
        Assert.Contains("&lt;strong&gt;bold text.", fixedText);
    }

}
