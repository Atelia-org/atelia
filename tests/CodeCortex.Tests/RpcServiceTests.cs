using Xunit;
using CodeCortex.Core.Index;
using CodeCortex.Core.Symbols;
using CodeCortex.Tests; // InMemoryFileSystem
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;

public class RpcServiceTests {
    private static string? ResolveOutline(CodeCortexIndex index, string outlineDir, InMemoryFileSystem fs, string query) {
        var resolver = new SymbolResolver(index);
        var match = resolver.Resolve(query, limit: 1);
        if (match.Count == 0) {
            return null;
        }

        var id = match[0].Id;
        var path = $"{outlineDir}/{id}.outline.md";
        if (!fs.FileExists(path)) {
            return null;
        }

        return fs.ReadAllText(path);
    }

    [Fact]
    public Task GetOutline_ReturnsContent_WhenOutlineExists() {
        var index = new CodeCortexIndex {
            Types = new List<TypeEntry> { new TypeEntry { Id = "T1", Fqn = "Test.Type", Kind = "class" } },
        };
        index.Maps.FqnIndex["Test.Type"] = "T1";

        var mockFs = new MockFileSystem();
        var fs = new InMemoryFileSystem(mockFs);
        string outlineDir = "/outlines";
        fs.CreateDirectory(outlineDir);
        fs.WriteAllText($"{outlineDir}/T1.outline.md", "OUTLINE_CONTENT");

        var content = ResolveOutline(index, outlineDir, fs, "Test.Type");
        Assert.Equal("OUTLINE_CONTENT", content);
        return Task.CompletedTask;
    }

    [Fact]
    public Task GetOutline_ReturnsNull_WhenTypeNotFound() {
        var index = new CodeCortexIndex();
        var fs = new InMemoryFileSystem(new MockFileSystem());
        var content = ResolveOutline(index, "/outlines", fs, "NotExist");
        Assert.Null(content);
        return Task.CompletedTask;
    }

    [Fact]
    public Task GetOutline_ReturnsNull_WhenOutlineFileMissing() {
        var index = new CodeCortexIndex {
            Types = new List<TypeEntry> { new TypeEntry { Id = "T2", Fqn = "Test.Type2", Kind = "class" } }
        };
        index.Maps.FqnIndex["Test.Type2"] = "T2";
        var fs = new InMemoryFileSystem(new MockFileSystem());
        var content = ResolveOutline(index, "/outlines", fs, "Test.Type2");
        Assert.Null(content);
        return Task.CompletedTask;
    }

    [Fact]
    public Task GetOutline_UsesGenericBase_WhenExactMatchFails() {
        var index = new CodeCortexIndex {
            Types = new List<TypeEntry> {
                new TypeEntry { Id = "T_GENERIC", Fqn = "System.Collections.Generic.List<T>", Kind = "class" }
            }
        };
        // Populate maps similar to IndexBuilder behavior
        index.Maps.FqnIndex["System.Collections.Generic.List<T>"] = "T_GENERIC";
        index.Maps.NameIndex["List<T>"] = new List<string> { "T_GENERIC" };
        index.Maps.GenericBaseNameIndex["List"] = new List<string> { "T_GENERIC" };

        var mockFs = new MockFileSystem();
        var fs = new InMemoryFileSystem(mockFs);
        string outlineDir = "/outlines";
        fs.CreateDirectory(outlineDir);
        fs.WriteAllText($"{outlineDir}/T_GENERIC.outline.md", "# Generic List Outline");

        var content = ResolveOutline(index, outlineDir, fs, "List");
        Assert.Equal("# Generic List Outline", content);
        return Task.CompletedTask;
    }
}
