using System.Threading.Tasks;
using Atelia.Analyzers.Style.Tests.TestHelpers;
using Xunit;

namespace Atelia.Analyzers.Style.Tests;

public class MT0008InlineBracesTests {
    private const string AnalyzerType = "Atelia.Analyzers.Style.MT0008InlineBracesAnalyzer";
    private static bool ContainsEither(string text, params string[] subs) => subs.Any(text.Contains);

    [Fact]
    public async Task If_Return_Inline() {
        var code = @"class C{void M(bool c){ if(c) return; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 允许 'if(c)' 与 '{' 之间存在 0-N 个空格
        Assert.Contains("if(c)", fixedText);
        Assert.True(ContainsEither(fixedText, "{ return; }", "{return; }", "{ return;}", "{return;}"));
    }

    [Fact]
    public async Task Else_If_Preserves_Structure_And_Braces_Body() {
        var code = @"class C{void M(bool a,bool b){ if(a) return; else if(b) return; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("else if(b)", fixedText);
        Assert.True(ContainsEither(fixedText, "{ return; }", "{return; }", "{ return;}", "{return;}"));
    }

    [Fact]
    public async Task Else_Single_Statement_Wrapped_Inline() {
        var code = @"class C{void M(bool a){ if(!a) return; else System.Console.WriteLine(1); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.True(ContainsEither(fixedText, "else {", "else{", "else  {"));
        Assert.Contains("System.Console.WriteLine(1);", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    public async Task Preserve_EndOfLine_Comment() {
        var code = @"class C{void M(bool c){ if(c) return; // note }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 行尾 // 注释应被转换为块注释放在闭括号之前："return; /* note */ }"
        Assert.Contains("/* note */", fixedText);
        Assert.Contains("return; /* note */ }", fixedText);
    }

    [Fact]
    public async Task Multiline_Embedded_Argument_List_Preserved_NoWrapAroundBraces() {
        var code = "class C{void M(bool c){ if(c) Foo(1,\n2); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.True(ContainsEither(fixedText, "{ Foo(1,", "{Foo(1,"));
        Assert.Contains("2); }", fixedText);
    }

    [Fact]
    public async Task Using_Chain_Outer_Exempt_Inner_Block_Untouched() {
        var code = @"using System; class C{void M(System.IO.Stream s){ using(s) using(var r = new System.IO.StreamReader(s)){ Console.WriteLine(r.ReadLine()); } }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 外层 using 不被包裹成 { using(...) { ... } }
        Assert.Contains("using(s) using(var r", fixedText);
        // 内层已有块保持原状
        Assert.Contains("{ Console.WriteLine", fixedText);
    }

    [Fact]
    public async Task Using_Chain_Final_NonBlock_Wrapped_Inline() {
        var code = @"using System; class C{void M(System.IO.Stream s){ using(s) using(var r = new System.IO.StreamReader(s)) Console.WriteLine(r.ReadLine()); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 外层仍为链式，末端单句被内联包裹
        Assert.Contains("using(s) using(var r", fixedText);
        Assert.True(ContainsEither(fixedText, "{ Console.WriteLine(r.ReadLine()); }", "{Console.WriteLine(r.ReadLine()); }"));
    }

    [Fact]
    public async Task If_ElseIf_Else_Chain_Preserves_Structure_But_Braces_Bodies() {
        var code = @"class C{int M(int x){ if(x==0) return 0; else if(x==1) return 1; else return -1; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 保留 else if 结构
        Assert.Contains("else if(x==1)", fixedText);
        // 分支体应为内联块：仅检查存在顺序 "{ ... return N; ... }" 即可
        void AssertBranch(string head, string body) {
            var iHead = fixedText.IndexOf(head);
            Assert.True(iHead >= 0);
            var iOpen = fixedText.IndexOf('{', iHead);
            Assert.True(iOpen > iHead);
            var iReturn = fixedText.IndexOf(body, iOpen);
            Assert.True(iReturn > iOpen);
            var iClose = fixedText.IndexOf('}', iReturn);
            Assert.True(iClose > iReturn);
        }
        AssertBranch("if(x==0)", "return 0;");
        AssertBranch("else if(x==1)", "return 1;");
        AssertBranch("else", "return -1;");
    }

    [Fact]
    public async Task If_Newline_Body_Preserved_Newline_Not_Removed() {
        var code = "class C{void M(bool c){ if(c)\nSystem.Console.WriteLine(1); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 原有换行保留，但应进入块内：换行位于 '{' 之后（作为语句的 leading 被迁移到 '{' 的 trailing）
        Assert.Contains("if(c) {", fixedText);
        Assert.Contains("{\nSystem.Console.WriteLine(1);", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    public async Task Else_Newline_Body_Preserved_Newline_Not_Removed() {
        var code = "class C{void M(bool a){ if(a) return; else\nSystem.Console.WriteLine(1); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 换行进入块内，位于 '{' 之后
        Assert.Contains("else {", fixedText);
        Assert.Contains("{\nSystem.Console.WriteLine(1);", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    public async Task While_Newline_Body_Preserved() {
        var code = "class C{void M(bool c){ while(c)\nDo(); }} void Do(){}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("while(c) {", fixedText);
        Assert.Contains("{\nDo();", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    public async Task ForEach_Newline_Body_Preserved() {
        var code = "class C{void M(string[] a){ foreach(var x in a)\nSystem.Console.WriteLine(x); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("foreach(var x in a) {", fixedText);
        Assert.Contains("{\nSystem.Console.WriteLine(x);", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    public async Task For_Newline_Body_Preserved() {
        var code = "class C{void M(){ for(int i=0;i<2;i++)\nSystem.Console.WriteLine(i); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("for(int i=0;i<2;i++) {", fixedText);
        Assert.Contains("{\nSystem.Console.WriteLine(i);", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    public async Task DoWhile_Newline_Body_Preserved() {
        var code = "class C{void M(bool c){ int i=0; do\nSystem.Console.WriteLine(i++); while(i<2); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("do {", fixedText);
        Assert.Contains("{\nSystem.Console.WriteLine(i++);", fixedText);
        // 验证 "} while" 的相对顺序（允许有空格差异）
        Assert.True(ContainsEither(fixedText, "} while", "}while") ||
                    (fixedText.IndexOf('}') >= 0 && fixedText.IndexOf("while(i<2)") > fixedText.IndexOf('}')));
    }

    [Fact]
    public async Task Label_Inside_Block_Is_Valid_And_Preserved() {
        var code = "class C{void M(bool c){ if(c)\nL1: return; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("if(c) {", fixedText);
        Assert.Contains("{\nL1:", fixedText);
        Assert.Contains("L1: return;", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    public async Task Leading_Comment_Before_Embedded_Statement_Remains_With_Newline() {
        var code = "class C{void M(bool c){ if(c)\n// note\nreturn; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 注释作为语句的 leading，被迁移到 '{' 之后（块内），并保留换行
        Assert.Contains("if(c) {", fixedText);
        Assert.Contains("{\n// note\nreturn; }", fixedText);
    }

    [Fact]
    public async Task EndOfLine_Comment_With_Block_Terminator_Sanitized() {
        var code = @"class C{void M(bool c){ if(c) Do(); // has */ token }} void Do(){}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 期望：将 '*/' 替换为 '* /' 后作为块注释放在 '}' 之前（确保无无效的终止符序列）
        Assert.Contains("/* has * / token */", fixedText);
    }

    // --- 边界用例补充 ---

    [Fact]
    public async Task Trailing_Mixed_Block_And_SingleLine_Comments_Merged_Before_Close() {
        var code = @"class C{void M(){ if(true) Foo(); /* A */ // B }} void Foo(){}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 断言：两个注释都在闭括号之前，且顺序保持（块注释原样 + // 转块注释）
        var iFoo = fixedText.IndexOf("Foo();");
        var iA = fixedText.IndexOf("/* A */", iFoo);
        var iB = fixedText.IndexOf("/* B */", iA);
        var iClose = fixedText.IndexOf('}', iB);
        Assert.True(iFoo >= 0 && iA > iFoo && iB > iA && iClose > iB);
    }

    [Fact]
    public async Task Using_Chain_Final_With_Eol_Comment_Wrapped_And_Comment_Before_Close() {
        var code = @"using System; class C{void M(System.IO.Stream s){ using(s) using(var r=new System.IO.StreamReader(s)) r.ReadLine(); // tail }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("using(s) using(var r", fixedText);
        Assert.Contains("/* tail */", fixedText);
        // 注释应位于闭括号之前
        var idx = fixedText.IndexOf("/* tail */");
        Assert.True(idx >= 0 && fixedText.IndexOf('}', idx) > idx);
    }

    [Fact]
    public async Task DoWhile_With_Eol_Comment_Sanitized_And_Placed_Before_Close() {
        var code = @"class C{void M(){ int i=0; do i++; // note */ while(i<2); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("/* note * / */", fixedText); // '*/' 被消毒为 '* /'
        Assert.True(ContainsEither(fixedText, "} while", "}while") || (fixedText.IndexOf('}') >= 0 && fixedText.IndexOf("while(i<2)") > fixedText.IndexOf('}')));
    }

    [Fact]
    public async Task Multiple_Leading_Comments_Preserved_Inside_Block_With_Newlines() {
        var code = "class C{void M(bool c){ if(c)\n// A\n/* B */\nreturn; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("if(c) {", fixedText);
        Assert.Contains("{\n// A\n/* B */\nreturn; }", fixedText);
    }

    [Fact]
    public async Task ForEach_Deconstruction_Newline_Preserved_Inside_Block() {
        var code = "class C{void M(){ (int,int)[] a=new[]{(1,2)}; foreach(var (x,y) in a)\nSystem.Console.WriteLine(x+y); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("foreach(var (x,y) in a) {", fixedText);
        Assert.Contains("{\nSystem.Console.WriteLine(x+y);", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    public async Task Trailing_Whitespace_Only_Minimum_Space_Before_Close() {
        var code = @"class C{void M(){ if(true) return;    }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 预期：无注释时，将多余空白折叠为最小空格，形如 "return; }"
        Assert.Contains("return; }", fixedText);
    }

    [Fact]
    public async Task Multiline_Comment_In_PreEol_Preserved_Before_Close() {
        var code = @"class C{void M(){ if(true) X(); /* multi\nline */ }} void X(){}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 多行注释保留原样，放在 '}' 之前
        Assert.Contains("/* multi\nline */", fixedText);
        var i = fixedText.IndexOf("/* multi\nline */");
        Assert.True(i >= 0 && fixedText.IndexOf('}', i) > i);
    }
}
