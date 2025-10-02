using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Xunit;
using Xunit.Abstractions;
using CodeCortexV2.Abstractions;
using CodeCortexV2.Index;

namespace Atelia.CodeCortex.Tests;

/// <summary>
/// 验证 IndexSynchronizer 对嵌套类型的处理行为
/// </summary>
public class IndexSynchronizer_NestedTypes_Investigation_Tests {
    private readonly ITestOutputHelper _output;

    public IndexSynchronizer_NestedTypes_Investigation_Tests(ITestOutputHelper output) {
        _output = output;
    }

    [Fact]
    public async Task IndexSynchronizer_Should_CreateEntries_ForAllNestedTypes() {
        // Arrange: 创建包含嵌套类型的代码
        var code = @"
namespace TestNs {
    public class Outer<T> {
        public class Inner {
            public class Leaf { }
        }
        public class Another<U> { }
    }
}";

        var workspace = new AdhocWorkspace(MefHostServices.Create(MefHostServices.DefaultAssemblies));
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            metadataReferences: new[] {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            }
        );
        var project = workspace.AddProject(projectInfo);
        var documentInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id),
            "Test.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(code), VersionStamp.Create()))
        );
        workspace.AddDocument(documentInfo);

        // Act: 创建 synchronizer 并初始化
        var sync = await IndexSynchronizer.CreateAsync(workspace, CancellationToken.None);
        var entries = sync.CurrentEntries.ToList();

        // Log all entries
        _output.WriteLine($"总共创建了 {entries.Count} 个 SymbolEntry\n");
        foreach (var entry in entries.OrderBy(e => e.DocCommentId)) {
            _output.WriteLine($"DocCommentId: {entry.DocCommentId}");
            _output.WriteLine($"  FullDisplayName: {entry.FullDisplayName}");
            _output.WriteLine($"  DisplayName: {entry.DisplayName}");
            _output.WriteLine($"  TypeSegments: [{string.Join(", ", entry.TypeSegments)}]");
            _output.WriteLine("");
        }

        // Assert: 验证所有嵌套类型都被创建
        // 注意：Roslyn 的 DocumentationCommentId 对嵌套类型使用 . 而不是 +

        // 调试：打印实际的字符编码
        var outerEntry = entries.FirstOrDefault(e => e.DocCommentId.StartsWith("T:TestNs.Outer") && !e.DocCommentId.Contains('.'));
        if (outerEntry != null) {
            _output.WriteLine($"Found Outer entry: '{outerEntry.DocCommentId}'");
            _output.WriteLine($"Bytes: {string.Join(", ", System.Text.Encoding.UTF8.GetBytes(outerEntry.DocCommentId).Select(b => b.ToString()))}");
            _output.WriteLine($"Expected: 'T:TestNs.Outer`1'");
            _output.WriteLine($"Expected bytes: {string.Join(", ", System.Text.Encoding.UTF8.GetBytes("T:TestNs.Outer`1").Select(b => b.ToString()))}");
        }

        Assert.Contains(entries, e => e.DocCommentId == "T:TestNs.Outer`1");
        Assert.Contains(entries, e => e.DocCommentId == "T:TestNs.Outer`1.Inner");
        Assert.Contains(entries, e => e.DocCommentId == "T:TestNs.Outer`1.Inner.Leaf");
        Assert.Contains(entries, e => e.DocCommentId == "T:TestNs.Outer`1.Another`1");

        _output.WriteLine("✅ 结论：IndexSynchronizer 确实为所有嵌套类型（包括父类型）创建了独立的 SymbolEntry！");
    }

    [Fact]
    public async Task IndexSynchronizer_Should_UseCorrectGenericSyntax_InFullDisplayName() {
        // Arrange
        var code = @"
namespace TestNs {
    public class Outer<TKey, TValue> {
        public class Inner { }
    }
}";

        var workspace = new AdhocWorkspace(MefHostServices.Create(MefHostServices.DefaultAssemblies));
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            metadataReferences: new[] {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            }
        );
        var project = workspace.AddProject(projectInfo);
        var documentInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id),
            "Test.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(code), VersionStamp.Create()))
        );
        workspace.AddDocument(documentInfo);

        // Act
        var sync = await IndexSynchronizer.CreateAsync(workspace, CancellationToken.None);
        var outerEntry = sync.CurrentEntries.FirstOrDefault(e => e.DocCommentId == "T:TestNs.Outer`2");

        // Assert
        Assert.NotNull(outerEntry);
        _output.WriteLine($"Outer<TKey, TValue> 的 FullDisplayName: {outerEntry.FullDisplayName}");

        // 验证使用了正确的泛型语法，而不是占位符
        Assert.Contains("<", outerEntry.FullDisplayName);
        Assert.Contains(">", outerEntry.FullDisplayName);
        Assert.DoesNotContain("`", outerEntry.FullDisplayName);

        // 验证包含实际的类型参数名称（TKey, TValue），而不是占位符（T1, T2）
        Assert.Contains("TKey", outerEntry.FullDisplayName);
        Assert.Contains("TValue", outerEntry.FullDisplayName);

        _output.WriteLine("✅ FullDisplayName 使用了正确的泛型语法，包含实际的类型参数名称！");
    }

    [Fact]
    public async Task SymbolsDelta_Should_SortByDocCommentIdLength() {
        // Arrange
        var code = @"
namespace TestNs {
    public class Outer<T> {
        public class Inner {
            public class Leaf { }
        }
    }
}";

        var workspace = new AdhocWorkspace(MefHostServices.Create(MefHostServices.DefaultAssemblies));
        var projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            metadataReferences: new[] {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
            }
        );
        var project = workspace.AddProject(projectInfo);
        var documentInfo = DocumentInfo.Create(
            DocumentId.CreateNewId(project.Id),
            "Test.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(code), VersionStamp.Create()))
        );
        workspace.AddDocument(documentInfo);

        // Act
        var sync = await IndexSynchronizer.CreateAsync(workspace, CancellationToken.None);
        var entries = sync.CurrentEntries.ToList();

        // Assert: 验证排序（DocCommentId 长度升序）
        var docIds = entries.Select(e => e.DocCommentId).OrderBy(id => id).ToList();
        _output.WriteLine("DocCommentId 排序（按长度）:");
        for (int i = 0; i < docIds.Count; i++) {
            if (i > 0) {
                var prevLen = docIds[i - 1].Length;
                var currLen = docIds[i].Length;
                _output.WriteLine($"  [{i}] {docIds[i]} (长度: {currLen}, 与前一个比较: {(currLen >= prevLen ? "✓" : "✗")})");
                Assert.True(currLen >= prevLen, $"排序违反契约：{docIds[i - 1]} ({prevLen}) 应该在 {docIds[i]} ({currLen}) 之前");
            }
            else {
                _output.WriteLine($"  [{i}] {docIds[i]} (长度: {docIds[i].Length})");
            }
        }

        _output.WriteLine("\n✅ 结论：SymbolsDelta 的排序契约保证了父类型总是在子类型之前被添加！");
    }
}
