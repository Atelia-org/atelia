using Xunit;
using CodeCortex.Service;
using CodeCortex.Core.Index;
using CodeCortex.Tests; // InMemoryFileSystem
using System.Collections.Generic;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;

public class RpcServiceTests {
    [Fact]
    public async Task GetOutlineAsync_ReturnsContent_WhenOutlineExistsAsync() {
        // 构造 index
        var index = new CodeCortexIndex {
            Types = new List<TypeEntry> { new TypeEntry { Id = "T1", Fqn = "Test.Type", Kind = "class" } }
        };

        // 构造内存文件系统并写入 outline
        var mockFs = new MockFileSystem();
        var fs = new InMemoryFileSystem(mockFs);
        string outlineDir = "/outlines";
        string outlinePath = $"{outlineDir}/T1.outline.md";
        fs.CreateDirectory(outlineDir);
        fs.WriteAllText(outlinePath, "OUTLINE_CONTENT");

        // 注入依赖
        var svc = new RpcService(index, outlineDir, fs);

        // 调用并断言
        var result = await svc.GetOutlineAsync(new OutlineRequest("T1"));
        Assert.Equal("OUTLINE_CONTENT", result);
    }

    [Fact]
    public async Task GetOutlineAsync_ReturnsNull_WhenTypeNotFoundAsync() {
        var index = new CodeCortexIndex();
        var fs = new InMemoryFileSystem(new MockFileSystem());
        var svc = new RpcService(index, "/outlines", fs);
        var result = await svc.GetOutlineAsync(new OutlineRequest("NotExist"));
        Assert.Null(result);
    }

    [Fact]
    public async Task GetOutlineAsync_ReturnsNull_WhenOutlineFileMissingAsync() {
        var index = new CodeCortexIndex {
            Types = new List<TypeEntry> { new TypeEntry { Id = "T2", Fqn = "Test.Type2", Kind = "class" } }
        };
        var fs = new InMemoryFileSystem(new MockFileSystem());
        var svc = new RpcService(index, "/outlines", fs);
        var result = await svc.GetOutlineAsync(new OutlineRequest("T2"));
        Assert.Null(result);
    }
}
