using System.Collections.Generic;
using CodeCortex.Core.Prompt;
using Xunit;

namespace Atelia.CodeCortex.Tests {
    public class PromptWindowBuilderTests {
        [Fact]
        public void BuildWindowFromContents_BasicOrderAndBudget() {
            var pinned = new List<string> { "PinnedA", "PinnedB" };
            var focus = new List<string> { "FocusA" };
            var recent = new List<string> { "RecentA", "RecentB", "RecentC" };
            var result = PromptWindowBuilder.BuildWindowFromContents(pinned, focus, recent, maxChars: 20);
            // 应优先保留 Pinned/Focus，Recent 只保留能容纳的部分
            Assert.Contains("# Pinned", result);
            Assert.Contains("PinnedA", result);
            Assert.Contains("PinnedB", result);
            Assert.Contains("# Focus", result);
            Assert.Contains("FocusA", result);
            Assert.Contains("# Recent", result);
            // 预算裁剪，Recent 可能被全部裁剪
            Assert.DoesNotContain("RecentB", result);
            Assert.DoesNotContain("RecentC", result);
        }

        [Fact]
        public void BuildWindowWithLoader_UsesLoaderAndSkipsNull() {
            var ids = new List<string> { "id1", "id2", "id3" };
            string? Loader(string id) => id == "id2" ? null : id.ToUpper();
            var result = PromptWindowBuilder.BuildWindowWithLoader(ids, new List<string>(), new List<string>(), Loader, 100);
            Assert.Contains("ID1", result);
            Assert.DoesNotContain("ID2", result);
            Assert.Contains("ID3", result);
        }
    }
}
