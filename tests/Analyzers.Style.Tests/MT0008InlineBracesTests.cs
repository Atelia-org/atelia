using System.Threading.Tasks;
using Atelia.Analyzers.Style.Tests.TestHelpers;
using Xunit;

namespace Atelia.Analyzers.Style.Tests;

/// <summary>
/// MT0008 内联花括号（Inline Braces）代码修复的单元测试集合：
/// - 主要目标是验证“插入花括号并保持语义”的行为，包括换行不增删但进入块内、注释迁移与消毒规则、链式结构（else-if/using）的保留或豁免。
/// - 空格数量属于正交问题：除非某个用例明确声明涉及空格，否则一律不在断言范围内，以避免与独立格式化策略耦合。
/// - 覆盖多种控制语句（if/else/for/foreach/while/do/using/lock/fixed）与若干边界用例，确保规则在不同形态下一致。
/// </summary>
public class MT0008InlineBracesTests {
    private const string AnalyzerType = "Atelia.Analyzers.Style.MT0008InlineBracesAnalyzer";
    private static bool ContainsEither(string text, params string[] subs) => subs.Any(text.Contains);

    [Fact]
    /// <summary>
    /// 单语句 if 应被内联花括号包裹；只关心插入花括号与内联形态，不关心空格数量。
    /// </summary>
    public async Task If_Return_Inline() {
        var code = @"class C{void M(bool c){ if(c) return; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 允许 'if(c)' 与 '{' 之间存在 0-N 个空格
        Assert.Contains("if(c)", fixedText);
        Assert.True(ContainsEither(fixedText, "{ return; }", "{return; }", "{ return;}", "{return;}"));
    }

    [Fact]
    /// <summary>
    /// 保持 else-if 链式结构不变，同时分支体改为内联块；不关心空格数量。
    /// </summary>
    public async Task Else_If_Preserves_Structure_And_Braces_Body() {
        var code = @"class C{void M(bool a,bool b){ if(a) return; else if(b) return; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("else if(b)", fixedText);
        Assert.True(ContainsEither(fixedText, "{ return; }", "{return; }", "{ return;}", "{return;}"));
    }

    [Fact]
    /// <summary>
    /// else 后的单语句应被包裹为内联块；断言不涉及空格数量，仅关注花括号插入与语句包含。
    /// </summary>
    public async Task Else_Single_Statement_Wrapped_Inline() {
        var code = @"class C{void M(bool a){ if(!a) return; else System.Console.WriteLine(1); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.True(ContainsEither(fixedText, "else {", "else{", "else  {"));
        Assert.Contains("System.Console.WriteLine(1);", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    /// <summary>
    /// 行尾 // 注释被转换为块注释并置于闭括号之前；不关心空格数量。
    /// </summary>
    public async Task Preserve_EndOfLine_Comment() {
        var code = @"class C{void M(bool c){ if(c) return; // note }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 行尾注释应被保留并位于闭括号之前（实现中转换为块注释，但空格形态可能不同）
        Assert.Contains("note", fixedText);
        var iNote = fixedText.IndexOf("note");
        var iClose = fixedText.IndexOf('}', iNote >= 0 ? iNote : 0);
        Assert.True(iNote >= 0 && iClose > iNote);
    }

    [Fact]
    /// <summary>
    /// 多行实参列表在内联块中保持，花括号不破坏多行参数的行结构；不关心空格数量。
    /// </summary>
    public async Task Multiline_Embedded_Argument_List_Preserved_NoWrapAroundBraces() {
        var code = "class C{void M(bool c){ if(c) Foo(1,\n2); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.True(ContainsEither(fixedText, "{ Foo(1,", "{Foo(1,"));
        Assert.Contains("2); }", fixedText);
    }

    [Fact]
    /// <summary>
    /// using 链外层豁免成块，内层已有块保持不变；不关心空格数量。
    /// </summary>
    public async Task Using_Chain_Outer_Exempt_Inner_Block_Untouched() {
        var code = @"using System; class C{void M(System.IO.Stream s){ using(s) using(var r = new System.IO.StreamReader(s)){ Console.WriteLine(r.ReadLine()); } }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 外层 using 不被包裹成 { using(...) { ... } }
        Assert.Contains("using(s) using(var r", fixedText);
        // 内层已有块保持原状
        Assert.Contains("{ Console.WriteLine", fixedText);
    }

    [Fact]
    /// <summary>
    /// using 链末端单句应被内联包裹，链结构保持；不关心空格数量。
    /// </summary>
    public async Task Using_Chain_Final_NonBlock_Wrapped_Inline() {
        var code = @"using System; class C{void M(System.IO.Stream s){ using(s) using(var r = new System.IO.StreamReader(s)) Console.WriteLine(r.ReadLine()); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 外层仍为链式，末端单句被内联包裹
        Assert.Contains("using(s) using(var r", fixedText);
        Assert.True(ContainsEither(fixedText, "{ Console.WriteLine(r.ReadLine()); }", "{Console.WriteLine(r.ReadLine()); }"));
    }

    [Fact]
    /// <summary>
    /// if/else if/else 链结构保持不变，所有分支体均为内联块；不关心空格数量。
    /// </summary>
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
    /// <summary>
    /// if 的换行不增加/删除，并迁移到块内（位于 "{" 之后）；不关心空格数量。
    /// </summary>
    public async Task If_Newline_Body_Preserved_Newline_Not_Removed() {
        var code = "class C{void M(bool c){ if(c)\nSystem.Console.WriteLine(1); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 原有换行保留：换行位于 '{' 之前（属于 if 的 trailing），语句与 '{' 同行
        Assert.Contains("if(c)\n {", fixedText);
        Assert.True(ContainsEither(fixedText, "{ System.Console.WriteLine(1);", "{System.Console.WriteLine(1);"));
        Assert.Contains("}", fixedText);
    }

    [Fact]
    /// <summary>
    /// else 的换行不增加/删除，并迁移到块内（位于 "{" 之后）；不关心空格数量。
    /// </summary>
    public async Task Else_Newline_Body_Preserved_Newline_Not_Removed() {
        var code = "class C{void M(bool a){ if(a) return; else\nSystem.Console.WriteLine(1); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 换行位于 '{' 之前，语句与 '{' 同行
        Assert.Contains("else\n {", fixedText);
        Assert.True(ContainsEither(fixedText, "{ System.Console.WriteLine(1);", "{System.Console.WriteLine(1);"));
        Assert.Contains("}", fixedText);
    }

    [Fact]
    /// <summary>
    /// while 的换行不增加/删除，并迁移到块内；不关心空格数量。
    /// </summary>
    public async Task While_Newline_Body_Preserved() {
        var code = "class C{void M(bool c){ while(c)\nDo(); }} void Do(){}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("while(c)\n {", fixedText);
        Assert.True(ContainsEither(fixedText, "{ Do();", "{Do();"));
        Assert.Contains("}", fixedText);
    }

    [Fact]
    /// <summary>
    /// foreach 的换行不增加/删除，并迁移到块内；不关心空格数量。
    /// </summary>
    public async Task ForEach_Newline_Body_Preserved() {
        var code = "class C{void M(string[] a){ foreach(var x in a)\nSystem.Console.WriteLine(x); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("foreach(var x in a)\n {", fixedText);
        Assert.True(ContainsEither(fixedText, "{ System.Console.WriteLine(x);", "{System.Console.WriteLine(x);"));
        Assert.Contains("}", fixedText);
    }

    [Fact]
    /// <summary>
    /// for 的换行不增加/删除，并迁移到块内；不关心空格数量。
    /// </summary>
    public async Task For_Newline_Body_Preserved() {
        var code = "class C{void M(){ for(int i=0;i<2;i++)\nSystem.Console.WriteLine(i); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("for(int i=0;i<2;i++)\n {", fixedText);
        Assert.True(ContainsEither(fixedText, "{ System.Console.WriteLine(i);", "{System.Console.WriteLine(i);"));
        Assert.Contains("}", fixedText);
    }

    [Fact]
    /// <summary>
    /// do-while 的单句体换行迁移到块内，且保持 "} while" 的语义顺序；不关心空格数量。
    /// </summary>
    public async Task DoWhile_Newline_Body_Preserved() {
        var code = "class C{void M(bool c){ int i=0; do\nSystem.Console.WriteLine(i++); while(i<2); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
    Assert.Contains("do\n {", fixedText);
    Assert.True(ContainsEither(fixedText, "{ System.Console.WriteLine(i++);", "{System.Console.WriteLine(i++);"));
        // 验证 "} while" 的相对顺序（允许有空格差异）
        Assert.True(ContainsEither(fixedText, "} while", "}while") ||
                    (fixedText.IndexOf('}') >= 0 && fixedText.IndexOf("while(i<2)") > fixedText.IndexOf('}')));
    }

    [Fact]
    /// <summary>
    /// 标签语句在块内依然有效；换行进入块内；不关心空格数量。
    /// </summary>
    public async Task Label_Inside_Block_Is_Valid_And_Preserved() {
        var code = "class C{void M(bool c){ if(c)\nL1: return; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("if(c)\n {", fixedText);
        Assert.Contains("L1: return;", fixedText);
        Assert.Contains("}", fixedText);
    }

    [Fact]
    /// <summary>
    /// 语句的 leading 注释保留并进入块内，且换行不变；不关心空格数量。
    /// </summary>
    public async Task Leading_Comment_Before_Embedded_Statement_Remains_With_Newline() {
        var code = "class C{void M(bool c){ if(c)\n// note\nreturn; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 注释作为语句的 leading，被迁移到块内，且 'if(c)' 的换行位于 '{' 之前
        Assert.Contains("if(c)\n {", fixedText);
        var iIf = fixedText.IndexOf("if(c)", StringComparison.Ordinal);
        var iBrace = fixedText.IndexOf('{', iIf >= 0 ? iIf : 0);
        Assert.True(iBrace > iIf);
        var iComment = fixedText.IndexOf("// note", iBrace);
        Assert.True(iComment > iBrace);
        var iReturnClose = fixedText.IndexOf("return; }", iComment);
        Assert.True(iReturnClose > iComment);
    }

    [Fact]
    /// <summary>
    /// 行尾注释包含 "*/" 时应消毒为 "* /"，并以块注释形态置于 "}" 之前；不关心空格数量。
    /// </summary>
    public async Task EndOfLine_Comment_With_Block_Terminator_Sanitized() {
        var code = @"class C{void M(bool c){ if(c) Do(); // has */ token }} void Do(){}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 期望：将 '*/' 替换为 '* /' 后作为块注释放在 '}' 之前（确保无无效的终止符序列）
        Assert.Contains("/* has", fixedText);
        Assert.Contains("* /", fixedText);
        var iSan = fixedText.IndexOf("* /");
        var iClose2 = fixedText.IndexOf('}', iSan >= 0 ? iSan : 0);
        Assert.True(iSan >= 0 && iClose2 > iSan);
    }

    // --- 边界用例补充 ---

    [Fact]
    /// <summary>
    /// 行内同时存在块注释与 // 注释（后者会转块注释）时，二者均置于闭括号之前且顺序保持；不关心空格数量。
    /// </summary>
    public async Task Trailing_Mixed_Block_And_SingleLine_Comments_Merged_Before_Close() {
        var code = @"class C{void M(){ if(true) Foo(); /* A */ // B }} void Foo(){}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 断言：两个注释都在闭括号之前，且顺序保持（块注释原样 + // 转块注释）
        var iFoo = fixedText.IndexOf("Foo();");
        Assert.True(iFoo >= 0);
        var iA = fixedText.IndexOf("/* A", iFoo);
        Assert.True(iA > iFoo);
        var iAEnd = fixedText.IndexOf("*/", iA);
        Assert.True(iAEnd > iA);
        var iB = fixedText.IndexOf("/* B", iAEnd);
        Assert.True(iB > iAEnd);
        var iClose = fixedText.IndexOf('}', iB);
        Assert.True(iClose > iB);
    }

    [Fact]
    /// <summary>
    /// using 链保持，末端单句被内联包裹，且行尾注释转块注释并置于闭括号之前；不关心空格数量。
    /// </summary>
    public async Task Using_Chain_Final_With_Eol_Comment_Wrapped_And_Comment_Before_Close() {
        var code = @"using System; class C{void M(System.IO.Stream s){ using(s) using(var r=new System.IO.StreamReader(s)) r.ReadLine(); // tail }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("using(s) using(var r", fixedText);
        Assert.Contains("/* tail", fixedText);
        // 注释应位于闭括号之前
        var idx = fixedText.IndexOf("/* tail");
        Assert.True(idx >= 0 && fixedText.IndexOf('}', idx) > idx);
    }

    [Fact]
    /// <summary>
    /// do-while 的行尾注释应被消毒并置于闭括号之前，同时保持 "} while" 的顺序；不关心空格数量。
    /// </summary>
    public async Task DoWhile_With_Eol_Comment_Sanitized_And_Placed_Before_Close() {
        var code = "class C{void M(){ int i=0; do i++; // note */ \nwhile(i<2); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("/* note * /", fixedText); // '*/' 被消毒为 '* /'
        Assert.True(ContainsEither(fixedText, "} while", "}while") || (fixedText.IndexOf('}') >= 0 && fixedText.IndexOf("while(i<2)") > fixedText.IndexOf('}')));
    }

    [Fact]
    public async Task DoWhile_With_Trailing_BlockComment_While_Remains_Code() {
        var code = @"class C{void M(){ int i=0; do i++; /* note */ while(i<2); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 该情形为同一行的块注释；注释应放到 '}' 之前；while 仍作为代码 token 出现在 '}' 之后
        var iComment = fixedText.IndexOf("/* note */", StringComparison.Ordinal);
        Assert.True(iComment >= 0);
        var iCloseBrace = fixedText.IndexOf('}', iComment);
        Assert.True(iCloseBrace > iComment);
        var iWhile = fixedText.IndexOf("while(i<2)", iCloseBrace, StringComparison.Ordinal);
        Assert.True(iWhile > iCloseBrace);
    }

    [Fact]
    /// <summary>
    /// 多段 leading 注释（如 // 与 /* */）应进入块内并保留顺序与换行；不关心空格数量。
    /// </summary>
    public async Task Multiple_Leading_Comments_Preserved_Inside_Block_With_Newlines() {
        var code = "class C{void M(bool c){ if(c)\n// A\n/* B */\nreturn; }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("if(c)\n {", fixedText);
        var iIf2 = fixedText.IndexOf("if(c)", StringComparison.Ordinal);
        var iBrace2 = fixedText.IndexOf('{', iIf2 >= 0 ? iIf2 : 0);
        Assert.True(iBrace2 > iIf2);
        var iA = fixedText.IndexOf("// A", iBrace2);
        Assert.True(iA > iBrace2);
        var iB2 = fixedText.IndexOf("/* B */", iA);
        Assert.True(iB2 > iA);
        var iRet = fixedText.IndexOf("return; }", iB2);
        Assert.True(iRet > iB2);
    }

    [Fact]
    /// <summary>
    /// 解构 foreach 的换行不增删并进入块内；不关心空格数量。
    /// </summary>
    public async Task ForEach_Deconstruction_Newline_Preserved_Inside_Block() {
        var code = "class C{void M(){ (int,int)[] a=new[]{(1,2)}; foreach(var (x,y) in a)\nSystem.Console.WriteLine(x+y); }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        Assert.Contains("foreach(var (x,y) in a)\n {", fixedText);
        Assert.True(ContainsEither(fixedText, "{ System.Console.WriteLine(x+y);", "{System.Console.WriteLine(x+y);"));
        Assert.Contains("}", fixedText);
    }

    [Fact]
    /// <summary>
    /// 尾随仅空白不应被保留为噪声，应折叠为最小空间从而形成可读的 "...; }"；总体不关心空格数量。
    /// </summary>
    public async Task Trailing_Whitespace_Only_Minimum_Space_Before_Close() {
        var code = @"class C{void M(){ if(true) return;    }}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 预期：无注释时，将多余空白折叠为最小空格，形如 "return; }"
        Assert.Contains("return; }", fixedText);
    }

    [Fact]
    /// <summary>
    /// 位于首个换行前的多行注释（preEol）应原样保留并安置在闭括号之前；不关心空格数量。
    /// </summary>
    public async Task Multiline_Comment_In_PreEol_Preserved_Before_Close() {
        var code = @"class C{void M(){ if(true) X(); /* multi\nline */ }} void X(){}";
        var fixedText = await AnalyzerTestHost.ApplyAllCodeFixesAsync(code, AnalyzerType, new Atelia.Analyzers.Style.CodeFixes.MT0008InlineBracesCodeFix(), "MT0008");
        // 多行注释保留原样，放在 '}' 之前（允许缩进差异）
        Assert.Contains("/* multi", fixedText);
        Assert.Contains("line */", fixedText);
        var i = fixedText.IndexOf("/* multi");
        var i2 = fixedText.IndexOf("line */", i >= 0 ? i : 0);
        Assert.True(i >= 0 && i2 > i && fixedText.IndexOf('}', i2) > i2);
    }
}
