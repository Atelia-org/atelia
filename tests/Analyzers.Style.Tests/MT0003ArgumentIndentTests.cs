using System.Collections.Generic;
using System.Linq;
using Atelia.Analyzers.Style;
using System.Threading.Tasks;
using Atelia.Analyzers.Style.Tests.TestHelpers;
using Microsoft.CodeAnalysis.CodeFixes;

namespace Atelia.Analyzers.Style.Tests;

public class MT0003ArgumentIndentTests {
    private const string Id = "MT0003";
    private static (IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> diags, Microsoft.CodeAnalysis.Compilation comp) RunAnalyzer(string src)
        => AnalyzerTestHost.RunAnalyzer(src, "Atelia.Analyzers.Style.MT0003ArgumentIndentAnalyzer");
    private static Task<string> ApplyAllCodeFixesAsync(string src)
        => AnalyzerTestHost.ApplyAllCodeFixesAsync(src, "Atelia.Analyzers.Style.MT0003ArgumentIndentAnalyzer", new MT0003ArgumentIndentCodeFix(), Id);

    // 1. 正确缩进：调用行有 8 个空格，参数行应 12 个空格 (indent_size=4)
    [Fact]
    public void ProperIndented_OneIndentBeyondInvocation() {
        var code = @"class C {
    void M(int a,int b){
        Foo(
            a,
            b);
    }
    void Foo(int a,int b){}
}";
        var (diags, _) = RunAnalyzer(code);
        Assert.DoesNotContain(diags, d => d.Id == Id);
    }

    // 2. 缩进不足：参数行仅与调用行对齐 (8 空格)，应诊断两行
    [Fact]
    public void MisIndented_TooShallow() {
        var code = @"class C {
    void M(int a,int b){
        Foo(
        a,
        b);
    }
    void Foo(int a,int b){}
}";
        var (diags, _) = RunAnalyzer(code);
        var target = diags.Where(d => d.Id == Id).ToList();
        Assert.Equal(2, target.Count);
    }

    // 3. 缩进过深：参数行 16 空格，应诊断
    [Fact]
    public void MisIndented_TooDeep() {
        var code = @"class C {
    void M(int a,int b){
        Foo(
                a,
                b);
    }
    void Foo(int a,int b){}
}";
        var (diags, _) = RunAnalyzer(code);
        var target = diags.Where(d => d.Id == Id).ToList();
        Assert.Equal(2, target.Count);
    }

    // 4. 同行多个参数：只检查行首，不应对第二第三个参数重复诊断
    [Fact]
    public void MultipleArgsOnSameLine_NoExtraDiagnostics() {
        var code = @"class C {
    void M(int a,int b,int c){
        Foo(
            a, b, c);
    }
    void Foo(int a,int b,int c){}
}";
        var (diags, _) = RunAnalyzer(code);
        // 该参数行缩进正确，应 0 诊断
        Assert.DoesNotContain(diags, d => d.Id == Id);
    }

    // 5. 注释行缩进错误 + 参数行一个错误（现策略：注释行被完全忽略，只剩参数行诊断）
    [Fact]
    public void CommentLine_MisIndent() {
        var code = @"class C {
    void M(){
        Foo(
  //comment
            1,
        2);
    }
    void Foo(int x,int y){}
}";
        var (diags, _) = RunAnalyzer(code);
        var target = diags.Where(d => d.Id == Id).ToList();
        // 期望仅 1 条：最后一行 '2)' 行首参数行
        Assert.Single(target);
    }

    // 6. 第一参数与 '(' 同行，只诊断后续不正确缩进
    [Fact]
    public void FirstArgumentOnOpenLine_Skip() {
        var code = @"class C {
    void M(int a,int b,int c){
        Foo(a,
        b,
            c);
    }
    void Foo(int a,int b,int c){}
}";
        var (diags, _) = RunAnalyzer(code);
        var target = diags.Where(d => d.Id == Id).ToList();
        // 只有 b 行缩进不正确 (应 12 实际 8)，c 行正确
        Assert.Single(target);
    }

    // 7. Lambda 参数块，仅检查 lambda 起始行 & 另一普通参数行
    [Fact]
    public void LambdaArgument_CheckStartLineOnly() {
        var code = @"using System; class C {
    void M(Action<int> act,int y){
        M2(
        x => {
            Console.WriteLine(x);
        },
        y);
    }
    void M2(Action<int> a,int b){}
}";
        var (diags, _) = RunAnalyzer(code);
        // 错误的行：lambda 起始行 (8 而应 12) + y 行 (8 而应 12) => 2
        Assert.Equal(2, diags.Count(d => d.Id == Id));
    }

    // CodeFix: 修复浅缩进
    [Fact]
    public async Task CodeFix_FixShallowIndent() {
        var code = @"class C {
    void M(){
        Foo(
        a,
        b);
    }
    void Foo(int a,int b){} int a=1,b=2;
}";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        // 验证 a,b 行都提升到 12 空格（调用行有 8 空格）
        Assert.Matches(@"Foo\(\r?\n {12}a,\r?\n {12}b\);", fixedText);
    }

    // CodeFix: 修复注释行 + 参数行
    [Fact]
    public async Task CodeFix_FixCommentLine() {
        var code = @"class C {
    void M(){
        Foo(
  //c
        a);
    }
    void Foo(int a){} int a=1;
}";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        // 当前实现：仅参数行保证修复；注释行可能保持原缩进（未来可改进）。
        Assert.Matches(@"Foo\(\r?\n {2}//c\r?\n {12}a\);", fixedText);
    }

    // CodeFix: 不改变已正确缩进
    [Fact]
    public async Task CodeFix_NoChangeWhenCorrect() {
        var code = @"class C {
    void M(){
        Foo(
            a);
    }
    void Foo(int a){} int a=1;
}";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        Assert.Equal(code, fixedText);
    }

    // 9. Declaration parameter list: method
    [Fact]
    public void DeclarationParameters_MisIndent() {
        var code = @"class C {
    void M(
    int a,
            int b,
        int c){}
}";
        var (diags, _) = RunAnalyzer(code);
        // Lines for a,b,c (excluding '(' line) => expect a & c mis-indented (a=8 vs expected 12? invocation line has 4; base line is 'void M(' line indent 4 so expected 8) compute specifically: line 'void M(' indentation 4, indentSize=4 expected 8. 'int a,' has 4 -> shallow; '            int b,' has 12 -> deep; '        int c)' has 8 correct. So a & b two diagnostics.
        Assert.Equal(2, diags.Count(d => d.Id == Id));
    }

    // 10. Declaration parameter list properly indented
    [Fact]
    public void DeclarationParameters_Proper() {
        var code = @"class C {
    void M(
        int a,
        int b,
        int c){}
}";
        var (diags, _) = RunAnalyzer(code);
        Assert.DoesNotContain(diags, d => d.Id == Id);
    }

    // CodeFix: 修复声明参数列表的混合缩进
    [Fact]
    public async Task CodeFix_DeclarationParameters_FixIndent() {
        var code = @"class C {
    void M(
    int a,
            int b,
        int c){}
}";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        // 期望 a,b,c 行统一 8 空格缩进（声明行 4 + indent_size 4）
        Assert.Matches(@"void M\(\r?\n {8}int a,\r?\n {8}int b,\r?\n {8}int c\)\{\}", fixedText);
    }

    // 8. FixAll-like scenario: multiple invocations w/ issues in one file
    [Fact]
    public async Task CodeFix_FixAll_LikeScenario() {
        var code = @"class C {
    void M(){
        Foo(
        a,
        b);
        Foo(
        a,
        b);
    }
    void Foo(int a,int b){} int a=1,b=2;
}";
        var fixedText = await ApplyAllCodeFixesAsync(code);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(fixedText, @"Foo\(\r?\n {12}a,\r?\n {12}b\);").Count);
    }
}
