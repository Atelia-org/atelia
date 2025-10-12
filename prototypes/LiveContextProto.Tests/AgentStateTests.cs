using System;
using System.Collections.Generic;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AgentStateTests {
    [Fact]
    public void AppendModelInput_AssignsTimestamp_AndRendersContext() {
        var baseTime = new DateTimeOffset(2025, 10, 12, 8, 0, 0, TimeSpan.Zero);
        var timestamps = new Queue<DateTimeOffset>(
            new[]
        {
            baseTime,
            baseTime.AddSeconds(1)
        }
        );

        var state = AgentState.CreateDefault(timestampProvider: () => timestamps.Dequeue());
        var sections = new List<KeyValuePair<string, string>>
        {
            new("default", "hello world")
        };

        var appended = state.AppendModelInput(new ModelInputEntry(sections));

        Assert.Equal(baseTime, appended.Timestamp);
        Assert.Single(state.History);
        Assert.Same(appended, state.History[0]);

        var context = state.RenderLiveContext();
        Assert.Equal(2, context.Count);
        Assert.IsAssignableFrom<ISystemMessage>(context[0]);
        Assert.Equal(ContextMessageRole.ModelInput, context[1].Role);
    }

    [Fact]
    public void AppendModelInput_Throws_WhenSectionsEmpty() {
        var state = AgentState.CreateDefault(timestampProvider: () => DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => state.AppendModelInput(new ModelInputEntry(Array.Empty<KeyValuePair<string, string>>())));
    }
}
