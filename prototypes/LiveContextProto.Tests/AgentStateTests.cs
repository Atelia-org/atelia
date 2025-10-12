using System;
using System.Collections.Generic;
using System.Linq;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.State.History;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AgentStateTests {
    [Fact]
    public void RenderLiveContext_IncludesSystemMessageAndChronologicalEntries() {
        var timestamps = new[] {
            DateTimeOffset.Parse("2025-10-12T10:00:00Z"),
            DateTimeOffset.Parse("2025-10-12T10:05:00Z"),
            DateTimeOffset.Parse("2025-10-12T10:10:00Z")
        };

        var state = CreateState(timestamps);

        var inputEntry = state.AppendModelInput(
            new ModelInputEntry(
                new[] {
                    new KeyValuePair<string, string>("default", "hello world")
        }
            )
        );

        var outputEntry = state.AppendModelOutput(
            new ModelOutputEntry(
                new[] { "hi there" },
                Array.Empty<ToolCallRequest>(),
                new ModelInvocationDescriptor("debug", "echo", "mock-model")
            )
        );

        var context = state.RenderLiveContext();

        Assert.Equal(3, context.Count);

        var system = Assert.IsAssignableFrom<ISystemMessage>(context[0]);
        Assert.Equal(timestamps[2], system.Timestamp);

        var input = Assert.IsAssignableFrom<IModelInputMessage>(context[1]);
        Assert.Equal(timestamps[0], input.Timestamp);
        Assert.Equal(inputEntry.Timestamp, input.Timestamp);

        var output = Assert.IsAssignableFrom<IModelOutputMessage>(context[2]);
        Assert.Equal(timestamps[1], output.Timestamp);
        Assert.Equal(outputEntry.Timestamp, output.Timestamp);
    }

    [Fact]
    public void RenderLiveContext_AttachesLiveScreenToLatestEligibleEntry() {
        var timestamps = new[] {
            DateTimeOffset.Parse("2025-10-12T11:00:00Z"),
            DateTimeOffset.Parse("2025-10-12T11:05:00Z"),
            DateTimeOffset.Parse("2025-10-12T11:10:00Z")
        };

        var state = CreateState(timestamps);
        state.UpdateMemoryNotebook("- 调试思路\n- 最近观察");

        state.AppendModelOutput(
            new ModelOutputEntry(
                new[] { "assistant draft" },
                Array.Empty<ToolCallRequest>(),
                new ModelInvocationDescriptor("debug", "echo", "mock-model")
            )
        );

        state.AppendModelInput(
            new ModelInputEntry(
                new[] {
                    new KeyValuePair<string, string>("default", "latest user input")
        }
            )
        );

        var context = state.RenderLiveContext();

        Assert.Equal(3, context.Count);

        var decoratedCount = context.Count(message => message is ILiveScreenCarrier);
        Assert.Equal(1, decoratedCount);

        var decorated = Assert.IsAssignableFrom<ILiveScreenCarrier>(context[^1]);
        Assert.False(string.IsNullOrWhiteSpace(decorated.LiveScreen));
        Assert.Contains("Live Screen", decorated.LiveScreen, StringComparison.Ordinal);
        Assert.Contains("Memory Notebook", decorated.LiveScreen, StringComparison.Ordinal);

        var innerInput = Assert.IsAssignableFrom<IModelInputMessage>(decorated.InnerMessage);
        Assert.Equal("latest user input", innerInput.ContentSections[0].Value);
    }

    [Fact]
    public void RenderLiveContext_SystemInstructionUpdateMaintainsLiveScreen() {
        var timestamps = new[] {
            DateTimeOffset.Parse("2025-10-12T14:00:00Z"),
            DateTimeOffset.Parse("2025-10-12T14:05:00Z"),
            DateTimeOffset.Parse("2025-10-12T14:10:00Z"),
            DateTimeOffset.Parse("2025-10-12T14:15:00Z")
        };

        var queue = new Queue<DateTimeOffset>(timestamps);
        var state = AgentState.CreateDefault(timestampProvider: () => queue.Dequeue());
        state.UpdateMemoryNotebook("- 继续保持 LiveScreen 校验");

        state.AppendModelInput(
            new ModelInputEntry(
                new[] {
                    new KeyValuePair<string, string>("default", "旧指令下的输入")
                }
            )
        );

        state.SetSystemInstruction("New system directive");

        state.AppendModelInput(
            new ModelInputEntry(
                new[] {
                    new KeyValuePair<string, string>("default", "新指令后的输入")
                }
            )
        );

        var context = state.RenderLiveContext();

        Assert.Equal(3, context.Count);

        var system = Assert.IsAssignableFrom<ISystemMessage>(context[0]);
        Assert.Equal("New system directive", system.Instruction);

        var decorated = Assert.IsAssignableFrom<ILiveScreenCarrier>(context[^1]);
        Assert.Contains("Memory Notebook", decorated.LiveScreen, StringComparison.Ordinal);
        var inputMessage = Assert.IsAssignableFrom<IModelInputMessage>(decorated.InnerMessage);
        Assert.Equal("新指令后的输入", inputMessage.ContentSections[0].Value);
    }

    [Fact]
    public void RenderLiveContext_LiveScreenAggregatesMultipleSections() {
        var timestamps = new[] {
            DateTimeOffset.Parse("2025-10-12T15:00:00Z"),
            DateTimeOffset.Parse("2025-10-12T15:05:00Z"),
            DateTimeOffset.Parse("2025-10-12T15:10:00Z"),
            DateTimeOffset.Parse("2025-10-12T15:15:00Z")
        };

        var queue = new Queue<DateTimeOffset>(timestamps);
        var state = AgentState.CreateDefault(timestampProvider: () => queue.Dequeue());
        state.UpdateMemoryNotebook("- Notebook 要点");
        state.UpdateLiveInfoSection("Planner Summary", "- 规划阶段：执行中");
        state.UpdateLiveInfoSection("Diagnostics", "Token usage compress pending");

        state.AppendModelInput(
            new ModelInputEntry(
                new[] {
                    new KeyValuePair<string, string>("default", "检查 LiveInfo 组合")
                }
            )
        );

        state.AppendModelOutput(
            new ModelOutputEntry(
                new[] { "ack" },
                Array.Empty<ToolCallRequest>(),
                new ModelInvocationDescriptor("stub", "spec", "stub-model")
            )
        );

        var context = state.RenderLiveContext();

        var liveScreenCarrier = Assert.Single(context.OfType<ILiveScreenCarrier>());
        var liveScreen = liveScreenCarrier.LiveScreen;
        Assert.False(string.IsNullOrWhiteSpace(liveScreen));
        Assert.Contains("## [Memory Notebook]", liveScreen!, StringComparison.Ordinal);
        Assert.Contains("## [Planner Summary]", liveScreen!, StringComparison.Ordinal);
        Assert.Contains("## [Diagnostics]", liveScreen!, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateLiveInfoSection_AddsAndRemovesSections() {
        var state = AgentState.CreateDefault(timestampProvider: () => DateTimeOffset.UtcNow);

        state.UpdateLiveInfoSection("Planner", "- step1");
        Assert.True(state.LiveInfoSections.ContainsKey("Planner"));

        state.UpdateLiveInfoSection("Planner", null);
        Assert.False(state.LiveInfoSections.ContainsKey("Planner"));
    }

    [Fact]
    public void Reset_ClearsLiveInfoAndNotebook() {
        var state = AgentState.CreateDefault(timestampProvider: () => DateTimeOffset.UtcNow);
        state.UpdateMemoryNotebook("- something");
        state.UpdateLiveInfoSection("Planner", "- summary");

        state.Reset();

        Assert.Empty(state.History);
        Assert.Empty(state.LiveInfoSections);
        Assert.Equal("（暂无 Memory Notebook 内容）", state.MemoryNotebookSnapshot);
    }

    [Fact]
    public void RenderLiveContext_SkipsLiveScreenWhenNotebookEmpty() {
        var timestamps = new[] {
            DateTimeOffset.Parse("2025-10-12T12:00:00Z"),
            DateTimeOffset.Parse("2025-10-12T12:05:00Z"),
            DateTimeOffset.Parse("2025-10-12T12:10:00Z")
        };

        var state = CreateState(timestamps);
        state.UpdateMemoryNotebook(null);

        state.AppendModelInput(
            new ModelInputEntry(
                new[] {
                    new KeyValuePair<string, string>("default", "ping")
        }
            )
        );

        state.AppendModelOutput(
            new ModelOutputEntry(
                new[] { "pong" },
                Array.Empty<ToolCallRequest>(),
                new ModelInvocationDescriptor("debug", "echo", "mock-model")
            )
        );

        var context = state.RenderLiveContext();

        Assert.Equal(3, context.Count);
        Assert.All(context, message => Assert.False(message is ILiveScreenCarrier));
    }

    [Fact]
    public void RenderLiveContext_AttachesLiveScreenToLatestToolResults() {
        var timestamps = new[] {
            DateTimeOffset.Parse("2025-10-12T13:00:00Z"),
            DateTimeOffset.Parse("2025-10-12T13:05:00Z"),
            DateTimeOffset.Parse("2025-10-12T13:10:00Z")
        };

        var state = CreateState(timestamps);
        state.UpdateMemoryNotebook("- Notebook synopsis -");

        state.AppendModelOutput(
            new ModelOutputEntry(
                new[] { "waiting for tool results" },
                Array.Empty<ToolCallRequest>(),
                new ModelInvocationDescriptor("debug", "echo", "mock-model")
            )
        );

        var results = new[] {
            new ToolCallResult(
                "memory.search",
                "toolcall-demo",
                ToolExecutionStatus.Success,
                "返回 1 条匹配",
                TimeSpan.FromMilliseconds(90)
            )
        };

        state.AppendToolResults(new ToolResultsEntry(results, null));

        var context = state.RenderLiveContext();

        Assert.Equal(3, context.Count);

        var decorated = Assert.IsAssignableFrom<ILiveScreenCarrier>(context[^1]);
        Assert.False(string.IsNullOrWhiteSpace(decorated.LiveScreen));

        var toolMessage = Assert.IsAssignableFrom<IToolResultsMessage>(decorated.InnerMessage);
        Assert.Single(toolMessage.Results);
        Assert.Equal(results[0].Result, toolMessage.Results[0].Result);
    }

    [Fact]
    public void AppendModelInput_Throws_WhenSectionsEmpty() {
        var state = AgentState.CreateDefault(timestampProvider: () => DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => state.AppendModelInput(new ModelInputEntry(Array.Empty<KeyValuePair<string, string>>())));
    }

    [Fact]
    public void AppendToolResults_Throws_WhenNoResultAndNoError() {
        var state = AgentState.CreateDefault(timestampProvider: () => DateTimeOffset.UtcNow);
        var entry = new ToolResultsEntry(Array.Empty<ToolCallResult>(), null);
        Assert.Throws<ArgumentException>(() => state.AppendToolResults(entry));
    }

    private static AgentState CreateState(IEnumerable<DateTimeOffset> timestamps) {
        var queue = new Queue<DateTimeOffset>(timestamps);
        return AgentState.CreateDefault(
            timestampProvider: () => queue.Count > 0
            ? queue.Dequeue()
            : throw new InvalidOperationException("No timestamps remaining in test provider.")
        );
    }
}
