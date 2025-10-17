using System.Linq;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class BaselineSnapshotTests {
    [Fact]
    public void AppendModelInput_AppendsEntryToHistory() {
        var state = AgentState.CreateDefault();
        var entry = state.AppendModelInput(new ModelInputEntry(LevelOfDetailSections.FromSingleSection("default", "hello world")));

        Assert.Equal(1, state.History.Count);
        var last = Assert.IsType<ModelInputEntry>(state.History[0]);
        Assert.Same(entry, last);
        var liveText = LevelOfDetailSections.ToPlainText(last.ContentSections.Live);
        Assert.Contains("hello world", liveText);
    }
}
