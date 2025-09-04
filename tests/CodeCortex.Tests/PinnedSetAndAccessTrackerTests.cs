using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CodeCortex.Core.Prompt;
using Xunit;

public class PinnedSetAndAccessTrackerTests {
    [Fact]
    public void PinnedSet_AddRemove_Persist() {
        var path = Path.GetTempFileName();
        try {
            var set = new PinnedSet<string>(path);
            Assert.True(set.Add("A"));
            Assert.True(set.Add("B"));
            Assert.False(set.Add("A"));
            Assert.True(set.Contains("A"));
            Assert.True(set.Remove("A"));
            Assert.False(set.Contains("A"));
            // 持久化校验
            var set2 = new PinnedSet<string>(path);
            Assert.True(set2.Contains("B"));
            Assert.False(set2.Contains("A"));
        } finally { File.Delete(path); }
    }

    [Fact]
    public void AccessTracker_LRU_Behavior() {
        var tracker = new AccessTracker<string>(3);
        tracker.Access("A");
        tracker.Access("B");
        tracker.Access("C");
        Assert.Equal(new[] { "C", "B", "A" }, tracker.GetAll());
        tracker.Access("B");
        Assert.Equal(new[] { "B", "C", "A" }, tracker.GetAll());
        tracker.Access("D");
        Assert.Equal(new[] { "D", "B", "C" }, tracker.GetAll());
    }
}
