using System.Threading.Tasks;
using MemoFileProto.Tools;

namespace Atelia.MemoFileProto.Tests.Tools;

public class MemoReplaceLiteralTests {
    [Fact]
    public async Task ExecuteAsync_ReplacesUniqueMatch_WhenOldTextUnique() {
        var store = new TestMemoryStore("项目状态：进行中\n任务数：5");
        var tool = store.CreateLiteralTool();

        var result = await tool.ExecuteAsync("""{"old_text": "项目状态：进行中", "new_text": "项目状态：已完成"}""");

        Assert.Equal("项目状态：已完成\n任务数：5", store.Value);
        Assert.Contains(ToolMessages.Updated, result);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenMultipleMatchesFound() {
        var store = new TestMemoryStore(
            """
void Func1() {
    int result = 0;
}
void Func2() {
    int result = 0;
}
"""
        );
        var tool = store.CreateLiteralTool();

        var result = await tool.ExecuteAsync("""{"old_text": "int result = 0;", "new_text": "int result = 1;"}""");

        Assert.Contains("Error: 找到", result);
        Assert.Equal(
            """
void Func1() {
    int result = 0;
}
void Func2() {
    int result = 0;
}
""", store.Value
        );
    }

    [Fact]
    public async Task ExecuteAsync_ReplacesFirstMatch_WhenSearchAfterEmptyString() {
        var store = new TestMemoryStore(
            """
__m256i vec = _mm256_loadu_si256(&data[0]);
__m256i vec = _mm256_loadu_si256(&data[8]);
"""
        );
        var tool = store.CreateLiteralTool();

        var result = await tool.ExecuteAsync("""{"old_text": "__m256i vec = _mm256_loadu_si256(&data[0]);", "new_text": "__m256i vec = _mm256_load_si256(&data[0]);", "search_after": ""}""");

        Assert.Contains(ToolMessages.Updated, result);
        Assert.Equal(
            """
__m256i vec = _mm256_load_si256(&data[0]);
__m256i vec = _mm256_loadu_si256(&data[8]);
""", store.Value
        );
    }

    [Fact]
    public async Task ExecuteAsync_ReplacesMatchAfterAnchor_WhenSearchAfterProvided() {
        var store = new TestMemoryStore(
            """
void Func1() {
    int result = 0;
}
void Func2() {
    int result = 0;
}
"""
        );
        var tool = store.CreateLiteralTool();

        var result = await tool.ExecuteAsync("""{"old_text": "int result = 0;", "new_text": "int result = 2;", "search_after": "void Func2() {"}""");

        Assert.Contains(ToolMessages.Updated, result);
        Assert.Equal(
            """
void Func1() {
    int result = 0;
}
void Func2() {
    int result = 2;
}
""", store.Value
        );
    }

    [Fact]
    public async Task ExecuteAsync_AppendsContent_WhenOldTextEmpty() {
        var store = new TestMemoryStore("## 第一章\n内容");
        var tool = store.CreateLiteralTool();

        var result = await tool.ExecuteAsync("""{"old_text": "", "new_text": "## 第二章\n新内容"}""");

        Assert.Contains(ToolMessages.ContentAppended, result);
        Assert.Equal("## 第一章\n内容\n## 第二章\n新内容", store.Value);
    }
}
