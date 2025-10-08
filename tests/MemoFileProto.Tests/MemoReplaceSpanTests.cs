using System.Threading.Tasks;

namespace Atelia.MemoFileProto.Tests.Tools;

public class MemoReplaceSpanTests {
    [Fact]
    public async Task ExecuteAsync_ReplacesRegion_WhenMarkersUnique() {
        var store = new TestMemoryStore(
            """
### 技术栈
旧内容
### 团队协作
"""
        );
        var tool = store.CreateSpanTool();

        var result = await tool.ExecuteAsync("""{"old_span_start": "### 技术栈", "old_span_end": "### 团队协作", "new_text": "### 技术栈\n主要使用 Rust\n### 团队协作"}""");

        Assert.Contains("记忆区域已更新", result);
        Assert.Equal(
            """
### 技术栈
主要使用 Rust
### 团队协作
""", store.Value
        );
    }

    [Fact]
    public async Task ExecuteAsync_ReplacesRegionAfterAnchor_WhenSearchAfterProvided() {
        var store = new TestMemoryStore(
            """
<!-- BEGIN -->
first block
<!-- END -->
-- marker --

<!-- BEGIN -->
second block
<!-- END -->
"""
        );
        var tool = store.CreateSpanTool();

        var result = await tool.ExecuteAsync("""{"old_span_start": "<!-- BEGIN -->", "old_span_end": "<!-- END -->", "new_text": "<!-- BEGIN -->\nupdated block\n<!-- END -->", "search_after": "-- marker --"}""");

        Assert.Contains("记忆区域已更新", result);
        Assert.Equal(
            """
<!-- BEGIN -->
first block
<!-- END -->
-- marker --

<!-- BEGIN -->
updated block
<!-- END -->
""", store.Value
        );
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenEndMarkerMissing() {
        var store = new TestMemoryStore(
            """
## Start
内容
"""
        );
        var tool = store.CreateSpanTool();

        var result = await tool.ExecuteAsync("""{"old_span_start": "## Start", "old_span_end": "## End", "new_text": "## Start\n新内容\n## End"}""");

        Assert.Contains("Error: 找不到 old_span_end", result);
        Assert.Equal(
            """
## Start
内容
""", store.Value
        );
    }
}
