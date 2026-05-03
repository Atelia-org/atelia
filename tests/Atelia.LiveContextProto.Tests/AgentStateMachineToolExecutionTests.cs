using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Atelia.Agent.Core;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;
using Atelia.Completion;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.LiveContextProto.Tests;

public sealed class AgentStateMachineToolExecutionTests {
    private const string StrategyId = "test-strategy";
    private const string ProviderId = "test-provider";
    private const string Specification = "spec";
    private const string Model = "model";

    [Fact]
    public async Task DoStepAsync_CompletesToolCallLifecycle() {
        var toolInvocations = new List<IReadOnlyDictionary<string, object?>?>();
        var echoTool = new DelegateTool(
            "echo",
            arguments => {
                toolInvocations.Add(arguments);
                return new LodToolExecuteResult(
                    ToolExecutionStatus.Success,
                    UniformContent("tool-output")
                );
            }
        );

        var firstResponse = CreateDeltaSequence(
            agg => agg.AppendContent("calling tool"),
            agg => agg.AppendToolCall(
                CreateToolCallRequest(
                    "echo",
                    "call-1",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["payload"] = "value" },
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["payload"] = "value" }
                )
            )
        );

        var secondResponse = CreateDeltaSequence(
            agg => agg.AppendContent("tool complete")
        );

        var provider = new FakeProviderClient(new[] { firstResponse, secondResponse });
        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 64_000u);
        var engine = CreateEngine(echoTool);
        var state = engine.State;

        engine.AppendNotification("hello world");

        var step1 = await engine.StepAsync(profile);
        Assert.True(step1.ProgressMade);
        Assert.NotNull(step1.Input);
        Assert.Equal(AgentRunState.WaitingInput, step1.StateBefore);
        Assert.Equal(AgentRunState.PendingInput, step1.StateAfter);

        var step2 = await engine.StepAsync(profile);
        Assert.True(step2.ProgressMade);
        Assert.NotNull(step2.Output);
        Assert.Equal(AgentRunState.PendingInput, step2.StateBefore);
        Assert.Equal(AgentRunState.WaitingToolResults, step2.StateAfter);
        Assert.Single(step2.Output!.Message.ToolCalls);

        var step3 = await engine.StepAsync(profile);
        Assert.True(step3.ProgressMade);
        Assert.Null(step3.Output);
        Assert.Equal(AgentRunState.WaitingToolResults, step3.StateBefore);
        Assert.Equal(AgentRunState.ToolResultsReady, step3.StateAfter);

        var step4 = await engine.StepAsync(profile);
        Assert.True(step4.ProgressMade);
        Assert.NotNull(step4.ToolResults);
        Assert.Equal(AgentRunState.ToolResultsReady, step4.StateBefore);
        Assert.Equal(AgentRunState.PendingToolResults, step4.StateAfter);

        var toolResults = step4.ToolResults!;
        Assert.Single(toolResults.Results);
        Assert.Null(toolResults.ExecuteError);

        var historyResult = toolResults.Results[0];
        Assert.Equal(ToolExecutionStatus.Success, historyResult.ExecuteResult.Status);
        var plainText = historyResult.ExecuteResult.Result.Basic;
        Assert.Contains("tool-output", plainText, StringComparison.Ordinal);

        Assert.Single(toolInvocations);
        var capturedArguments = toolInvocations[0];
        Assert.NotNull(capturedArguments);
        Assert.Equal("value", capturedArguments!["payload"]);

        Assert.Equal("call-1", historyResult.ToolCallId);
        Assert.Equal("echo", historyResult.ToolName);

        var step5 = await engine.StepAsync(profile);
        Assert.True(step5.ProgressMade);
        Assert.NotNull(step5.Output);
        Assert.Equal(AgentRunState.PendingToolResults, step5.StateBefore);
        Assert.Equal(AgentRunState.WaitingInput, step5.StateAfter);

        Assert.Equal(4, state.RecentHistory.Count);
        Assert.IsType<ObservationEntry>(state.RecentHistory[0]);
        Assert.IsType<ActionEntry>(state.RecentHistory[1]);
        Assert.IsType<ToolResultsEntry>(state.RecentHistory[2]);
        Assert.IsType<ActionEntry>(state.RecentHistory[3]);
    }

    [Fact]
    public async Task DoStepAsync_ToolFailureProducesExecuteError() {
        var failingTool = new DelegateTool(
            "broken",
            _ => new LodToolExecuteResult(
                ToolExecutionStatus.Failed,
                UniformContent("tool failed")
            )
        );

        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(
                    agg => agg.AppendContent("call broken"),
                    agg => agg.AppendToolCall(CreateToolCallRequest("broken", "fail-1", ImmutableDictionary<string, string>.Empty))
                )
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 64_000u);
        var engine = CreateEngine(failingTool);

        engine.AppendNotification("trigger failure");

        await engine.StepAsync(profile); // WaitingInput -> PendingInput
        await engine.StepAsync(profile); // PendingInput -> WaitingToolResults
        await engine.StepAsync(profile); // WaitingToolResults -> ToolResultsReady

        var step4 = await engine.StepAsync(profile);
        Assert.True(step4.ProgressMade);
        Assert.NotNull(step4.ToolResults);
        Assert.Equal(AgentRunState.ToolResultsReady, step4.StateBefore);
        Assert.Equal(AgentRunState.PendingToolResults, step4.StateAfter);

        var toolResults = step4.ToolResults!;
        Assert.Equal("tool failed", toolResults.ExecuteError);
        Assert.Equal(ToolExecutionStatus.Failed, toolResults.Results.Single().ExecuteResult.Status);
    }

    [Fact]
    public async Task StepAsync_HiddenToolsAreExcludedFromCompletionRequest() {
        var visibleTool = new DelegateTool(
            "visible",
            _ => new LodToolExecuteResult(ToolExecutionStatus.Success, UniformContent("unused"))
        );

        var hiddenTool = new DelegateTool(
            "hidden",
            _ => new LodToolExecuteResult(ToolExecutionStatus.Success, UniformContent("unused"))
        ) {
            Visible = false
        };

        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(agg => agg.AppendContent("no tool call"))
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 64_000u);
        var engine = CreateEngine(visibleTool, hiddenTool);

        engine.AppendNotification("trigger model call");

        await engine.StepAsync(profile); // WaitingInput -> PendingInput
        await engine.StepAsync(profile); // PendingInput -> WaitingInput / ActionEntry

        var capturedRequest = Assert.Single(provider.CapturedRequests);
        Assert.Equal(2, capturedRequest.Tools.Length);
        Assert.Contains(capturedRequest.Tools, definition => definition.Name.Equals("ctx_compress", StringComparison.Ordinal));
        Assert.Contains(capturedRequest.Tools, definition => definition.Name.Equals("visible", StringComparison.Ordinal));
        Assert.DoesNotContain(capturedRequest.Tools, definition => definition.Name.Equals("hidden", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StepAsync_TogglingVisibilityUsesCachedDefinitions() {
        var toggleTool = new DelegateTool(
            "toggle",
            _ => new LodToolExecuteResult(ToolExecutionStatus.Success, UniformContent("unused"))
        );

        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(agg => agg.AppendContent("cycle-1")),
                CreateDeltaSequence(agg => agg.AppendContent("cycle-2")),
                CreateDeltaSequence(agg => agg.AppendContent("cycle-3"))
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 64_000u);
        var engine = CreateEngine(toggleTool);

        static Task AdvanceAsync(AgentEngine engine, LlmProfile profile, string notification)
            => AdvanceInternalAsync(engine, profile, notification);

        static async Task AdvanceInternalAsync(AgentEngine engine, LlmProfile profile, string notification) {
            engine.AppendNotification(notification);
            await engine.StepAsync(profile);
            await engine.StepAsync(profile);
        }

        await AdvanceAsync(engine, profile, "notification-1");
        toggleTool.Visible = false;

        await AdvanceAsync(engine, profile, "notification-2");
        toggleTool.Visible = true;

        await AdvanceAsync(engine, profile, "notification-3");

        var captured = provider.CapturedRequests;
        Assert.Equal(3, captured.Count);

        var first = captured[0].Tools;
        var second = captured[1].Tools;
        var third = captured[2].Tools;

        Assert.Equal(2, first.Length);
        var definition = Assert.Single(first.Where(definition => definition.Name.Equals("toggle", StringComparison.Ordinal)));

        Assert.Single(second);
        Assert.Equal("ctx_compress", second[0].Name);

        Assert.Equal(2, third.Length);
        var thirdDefinition = Assert.Single(third.Where(definition => definition.Name.Equals("toggle", StringComparison.Ordinal)));

        Assert.Same(definition, thirdDefinition);
    }

    [Fact]
    public async Task PrepareInvocationAsync_RefreshesWindowAndToolVisibility_ForCurrentPendingInputCall() {
        var preparedTool = new DelegateTool(
            "prepared",
            _ => new LodToolExecuteResult(ToolExecutionStatus.Success, UniformContent("unused"))
        ) {
            Visible = false
        };

        var preparedApp = new PrepareAwareApp(preparedTool) {
            Snapshot = "stale"
        };

        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(agg => agg.AppendContent("ok"))
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 64_000u);
        var engine = new AgentEngine();
        engine.RegisterApp(preparedApp);
        engine.PrepareInvocationAsync = async (_, cancellationToken) => {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            preparedApp.Snapshot = "fresh";
            preparedTool.Visible = true;
        };

        engine.AppendNotification("trigger prepare");

        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        var request = Assert.Single(provider.CapturedRequests);
        var windowMessage = Assert.Single(
            request.Context.OfType<ObservationMessage>(),
            message => message.Content is not null && message.Content.Contains("PrepareWindow:", StringComparison.Ordinal)
        );

        Assert.Contains("fresh", windowMessage.Content, StringComparison.Ordinal);
        Assert.Contains(request.Tools, definition => definition.Name.Equals("prepared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareInvocationAsync_RunsAgain_ForPendingToolResultsModelCall() {
        var echoTool = new DelegateTool(
            "echo",
            _ => new LodToolExecuteResult(ToolExecutionStatus.Success, UniformContent("tool-output"))
        );
        var preparedApp = new PrepareAwareApp();

        var provider = new FakeProviderClient(new[] {
            CreateDeltaSequence(
                agg => agg.AppendContent("calling tool"),
                agg => agg.AppendToolCall(CreateToolCallRequest("echo", "call-1", ImmutableDictionary<string, string>.Empty))
            ),
            CreateDeltaSequence(agg => agg.AppendContent("done"))
        });

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 64_000u);
        var engine = new AgentEngine();
        engine.RegisterApp(preparedApp);
        engine.RegisterTool(echoTool);

        var prepareCount = 0;
        engine.PrepareInvocationAsync = async (_, cancellationToken) => {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            prepareCount++;
            preparedApp.Snapshot = $"snapshot-{prepareCount}";
        };

        engine.AppendNotification("trigger tool");

        await engine.StepAsync(profile); // WaitingInput -> PendingInput
        await engine.StepAsync(profile); // PendingInput -> WaitingToolResults (prepare #1)
        await engine.StepAsync(profile); // WaitingToolResults -> ToolResultsReady
        await engine.StepAsync(profile); // ToolResultsReady -> PendingToolResults
        await engine.StepAsync(profile); // PendingToolResults -> WaitingInput (prepare #2)

        Assert.Equal(2, prepareCount);
        Assert.Equal(2, provider.CapturedRequests.Count);

        var firstWindow = Assert.Single(
            provider.CapturedRequests[0].Context.OfType<ObservationMessage>(),
            message => message.Content is not null && message.Content.Contains("PrepareWindow:", StringComparison.Ordinal)
        );
        var secondWindow = Assert.Single(
            provider.CapturedRequests[1].Context.OfType<ObservationMessage>(),
            message => message.Content is not null && message.Content.Contains("PrepareWindow:", StringComparison.Ordinal)
        );

        Assert.Contains("snapshot-1", firstWindow.Content, StringComparison.Ordinal);
        Assert.Contains("snapshot-2", secondWindow.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextCompressionWindow_IncludesEstimatedCompactionRangePreview() {
        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(agg => agg.AppendContent("turn-1 reply")),
                CreateDeltaSequence(agg => agg.AppendContent("turn-2 reply")),
                CreateDeltaSequence(agg => agg.AppendContent("turn-3 reply"))
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 1u);
        var engine = CreateEngine();

        engine.AppendNotification("turn-1 note");
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        engine.AppendNotification("turn-2 note");
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        engine.AppendNotification("turn-3 note");
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        var request = provider.CapturedRequests[2];
        var window = Assert.Single(
            request.Context.OfType<ObservationMessage>(),
            message => message.Content is not null && message.Content.Contains("## ContextCompression", StringComparison.Ordinal)
        );

        Assert.Contains("预计压缩范围", window.Content, StringComparison.Ordinal);
        Assert.Contains("边界前最后一条", window.Content, StringComparison.Ordinal);
        Assert.Contains("边界后第一条保留内容", window.Content, StringComparison.Ordinal);
        Assert.Contains("turn-2 note", window.Content, StringComparison.Ordinal);
        Assert.Contains("turn-2 reply", window.Content, StringComparison.Ordinal);
        Assert.Contains("keep_hints", window.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareInvocationAsync_CanCancelCurrentModelCall() {
        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(agg => agg.AppendContent("should-not-run"))
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 64_000u);
        var engine = new AgentEngine();
        engine.PrepareInvocationAsync = (args, _) => {
            args.Cancel = true;
            return Task.CompletedTask;
        };

        engine.AppendNotification("trigger cancel");

        await engine.StepAsync(profile);
        var step = await engine.StepAsync(profile);

        Assert.False(step.ProgressMade);
        Assert.Null(step.Output);
        Assert.Empty(provider.CapturedRequests);
        Assert.Equal(AgentRunState.PendingInput, step.StateAfter);
    }

    [Fact]
    public async Task PrepareInvocationAsync_RequestCompaction_DefersCurrentModelCallUntilCompactionCompletes() {
        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(agg => agg.AppendContent("turn-1 reply")),
                CreateDeltaSequence(agg => agg.AppendContent("turn-2 reply")),
                CreateDeltaSequence(agg => agg.AppendContent("summary-text")),
                CreateDeltaSequence(agg => agg.AppendContent("after-prepare-compaction"))
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 100_000u);
        var engine = CreateEngine();

        engine.AppendNotification("turn-1");
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        engine.AppendNotification("turn-2");
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        var compactionRequested = false;
        engine.PrepareInvocationAsync = (_, _) => {
            if (compactionRequested) {
                return Task.CompletedTask;
            }

            compactionRequested = true;
            engine.RequestCompaction("custom-system", "custom-summarize");
            return Task.CompletedTask;
        };

        engine.AppendNotification("turn-3");
        await engine.StepAsync(profile);

        var deferredStep = await engine.StepAsync(profile);
        Assert.True(deferredStep.ProgressMade);
        Assert.Null(deferredStep.Output);
        Assert.Equal(AgentRunState.PendingInput, deferredStep.StateBefore);
        Assert.Equal(AgentRunState.Compacting, deferredStep.StateAfter);
        Assert.Equal(2, provider.CapturedRequests.Count);

        var compactingStep = await engine.StepAsync(profile);
        Assert.True(compactingStep.ProgressMade);
        Assert.Equal(AgentRunState.Compacting, compactingStep.StateBefore);
        Assert.Equal(AgentRunState.PendingInput, compactingStep.StateAfter);
        Assert.Equal(3, provider.CapturedRequests.Count);
        Assert.Equal("custom-system", provider.CapturedRequests[2].SystemPrompt);

        var finalStep = await engine.StepAsync(profile);
        Assert.True(finalStep.ProgressMade);
        Assert.NotNull(finalStep.Output);
        Assert.Equal(AgentRunState.PendingInput, finalStep.StateBefore);
        Assert.Equal(AgentRunState.WaitingInput, finalStep.StateAfter);
        Assert.Equal(4, provider.CapturedRequests.Count);
        Assert.Equal("after-prepare-compaction", finalStep.Output!.Message.GetFlattenedText());
    }

    [Fact]
    public async Task StepAsync_ForwardsCompletionObserverToProvider() {
        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(
                    agg => agg.AppendContent("streamed-text"),
                    agg => agg.AppendToolCall(
                        CreateToolCallRequest(
                            "echo",
                            "call-stream",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["payload"] = "value" }
                        )
                    )
                )
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 64_000u);
        var engine = CreateEngine();
        var observedText = string.Empty;
        RawToolCall? observedToolCall = null;

        engine.AppendNotification("trigger stream observer");
        await engine.StepAsync(profile);

        var observer = new CompletionStreamObserver();
        observer.ReceivedTextDelta += delta => observedText += delta;
        observer.ReceivedToolCall += toolCall => observedToolCall = toolCall;

        var step = await engine.StepAsync(profile, observer);

        Assert.NotNull(step.Output);
        Assert.Equal("streamed-text", observedText);
        Assert.NotNull(observedToolCall);
        Assert.Equal("echo", observedToolCall!.ToolName);
        Assert.Equal("{\"payload\":\"value\"}", observedToolCall.RawArgumentsJson);
    }

    [Fact]
    public async Task CtxCompress_ExecutesImmediateCompactionWithinToolStep() {
        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(agg => agg.AppendContent("turn-1")),
                CreateDeltaSequence(agg => agg.AppendContent("turn-2")),
                CreateDeltaSequence(
                    agg => agg.AppendContent("compress now"),
                    agg => agg.AppendToolCall(
                        CreateToolCallRequest(
                            "ctx_compress",
                            "compress-1",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                                ["keep_hints"] = "保留当前目标",
                                ["forget_hints"] = "淡化已完成试错"
                            }
                        )
                    )
                ),
                CreateDeltaSequence(agg => agg.AppendContent("summary-text")),
                CreateDeltaSequence(agg => agg.AppendContent("after-compress"))
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 100u);
        var engine = CreateEngine();

        engine.AppendNotification("turn-1");
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        engine.AppendNotification("turn-2");
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        Assert.Equal(4, engine.State.RecentHistory.Count);

        engine.AppendNotification("turn-3");
        await engine.StepAsync(profile);

        var toolCallStep = await engine.StepAsync(profile);
        Assert.Equal(AgentRunState.PendingInput, toolCallStep.StateBefore);
        Assert.Equal(AgentRunState.WaitingToolResults, toolCallStep.StateAfter);
        Assert.Equal(3, provider.CapturedRequests.Count);

        var historyCountBeforeCompaction = engine.State.RecentHistory.Count;
        var executeToolStep = await engine.StepAsync(profile);
        Assert.Equal(AgentRunState.WaitingToolResults, executeToolStep.StateBefore);
        Assert.Equal(AgentRunState.ToolResultsReady, executeToolStep.StateAfter);
        Assert.Equal(4, provider.CapturedRequests.Count);
        Assert.True(engine.State.RecentHistory.Count < historyCountBeforeCompaction);

        var toolResultsStep = await engine.StepAsync(profile);
        Assert.Equal(AgentRunState.ToolResultsReady, toolResultsStep.StateBefore);
        Assert.Equal(AgentRunState.PendingToolResults, toolResultsStep.StateAfter);
        var toolResult = Assert.Single(toolResultsStep.ToolResults!.Results);
        Assert.Equal("ctx_compress", toolResult.ToolName);
        Assert.Equal(ToolExecutionStatus.Success, toolResult.ExecuteResult.Status);
        Assert.Contains("上下文压缩成功", toolResult.ExecuteResult.Result.Basic, StringComparison.Ordinal);
        Assert.Contains("释放了约", toolResult.ExecuteResult.Result.Basic, StringComparison.Ordinal);

        var finalModelStep = await engine.StepAsync(profile);
        Assert.Equal(AgentRunState.PendingToolResults, finalModelStep.StateBefore);
        Assert.Equal(AgentRunState.WaitingInput, finalModelStep.StateAfter);
        Assert.Equal(5, provider.CapturedRequests.Count);
        Assert.NotNull(finalModelStep.Output);
        Assert.Equal("after-compress", finalModelStep.Output!.Message.GetFlattenedText());
    }

    [Fact]
    public async Task CtxCompress_ImmediateCompactionReusesResolvedProfileFromCurrentTurn() {
        var initialClient = new FakeProviderClient(Array.Empty<Action<CompletionAggregator>[]>());
        var resolvedClient = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(agg => agg.AppendContent("turn-1")),
                CreateDeltaSequence(agg => agg.AppendContent("turn-2")),
                CreateDeltaSequence(
                    agg => agg.AppendContent("compress now"),
                    agg => agg.AppendToolCall(
                        CreateToolCallRequest(
                            "ctx_compress",
                            "compress-1",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                                ["keep_hints"] = "保留当前目标"
                            }
                        )
                    )
                ),
                CreateDeltaSequence(agg => agg.AppendContent("summary-text")),
                CreateDeltaSequence(agg => agg.AppendContent("after-compress"))
            }
        );

        var initialProfile = new LlmProfile(Client: initialClient, ModelId: Model, Name: "initial", SoftContextTokenCap: 100u);
        var resolvedProfile = new LlmProfile(Client: resolvedClient, ModelId: Model, Name: "resolved", SoftContextTokenCap: 100u);
        var engine = CreateEngine();
        engine.ResolveProfile += (_, args) => { args.Profile = resolvedProfile; };

        engine.AppendNotification("turn-1");
        await engine.StepAsync(initialProfile);
        await engine.StepAsync(initialProfile);

        engine.AppendNotification("turn-2");
        await engine.StepAsync(initialProfile);
        await engine.StepAsync(initialProfile);

        engine.AppendNotification("turn-3");
        await engine.StepAsync(initialProfile);
        await engine.StepAsync(initialProfile);
        await engine.StepAsync(initialProfile);

        var toolResultsStep = await engine.StepAsync(initialProfile);
        var toolResult = Assert.Single(toolResultsStep.ToolResults!.Results);
        Assert.Equal(ToolExecutionStatus.Success, toolResult.ExecuteResult.Status);
        Assert.Contains("上下文压缩成功", toolResult.ExecuteResult.Result.Basic, StringComparison.Ordinal);

        var finalModelStep = await engine.StepAsync(initialProfile);
        Assert.NotNull(finalModelStep.Output);
        Assert.Equal("after-compress", finalModelStep.Output!.Message.GetFlattenedText());

        Assert.Empty(initialClient.CapturedRequests);
        Assert.Equal(5, resolvedClient.CapturedRequests.Count);
    }

    [Fact]
    public async Task CtxCompress_UsesSplitPointFromPreviewSeenByModel() {
        var hugeCurrentActionText = new string('x', 12_000);
        var provider = new FakeProviderClient(
            new[] {
                CreateDeltaSequence(agg => agg.AppendContent("turn-1 reply")),
                CreateDeltaSequence(agg => agg.AppendContent("turn-2 reply")),
                CreateDeltaSequence(
                    agg => agg.AppendContent(hugeCurrentActionText),
                    agg => agg.AppendToolCall(
                        CreateToolCallRequest(
                            "ctx_compress",
                            "compress-1",
                            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                                ["keep_hints"] = "保留当前目标"
                            }
                        )
                    )
                ),
                CreateDeltaSequence(agg => agg.AppendContent("summary-text"))
            }
        );

        var profile = new LlmProfile(Client: provider, ModelId: Model, Name: StrategyId, SoftContextTokenCap: 1u);
        var engine = CreateEngine();

        engine.AppendNotification("turn-1 note");
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        engine.AppendNotification("turn-2 note");
        await engine.StepAsync(profile);
        await engine.StepAsync(profile);

        engine.AppendNotification("turn-3 note");
        await engine.StepAsync(profile);

        var previewBeforeModelCall = engine.BuildCompactionPreview();
        Assert.True(previewBeforeModelCall.HasValue);
        Assert.Equal(3, previewBeforeModelCall.Value.SplitIndex);

        var toolCallStep = await engine.StepAsync(profile);
        Assert.Equal(AgentRunState.PendingInput, toolCallStep.StateBefore);
        Assert.Equal(AgentRunState.WaitingToolResults, toolCallStep.StateAfter);

        var previewWindow = Assert.Single(
            provider.CapturedRequests[2].Context.OfType<ObservationMessage>(),
            message => message.Content is not null && message.Content.Contains("## ContextCompression", StringComparison.Ordinal)
        );
        Assert.Contains("预计压缩范围", previewWindow.Content, StringComparison.Ordinal);

        var previewAfterCurrentAction = engine.BuildCompactionPreview();
        Assert.True(previewAfterCurrentAction.HasValue);
        Assert.Equal(5, previewAfterCurrentAction.Value.SplitIndex);

        var executeToolStep = await engine.StepAsync(profile);
        Assert.Equal(AgentRunState.WaitingToolResults, executeToolStep.StateBefore);
        Assert.Equal(AgentRunState.ToolResultsReady, executeToolStep.StateAfter);

        var summaryRequest = provider.CapturedRequests[3];
        Assert.Equal(previewBeforeModelCall.Value.SplitIndex + 1, summaryRequest.Context.Count);
        Assert.NotEqual(previewAfterCurrentAction.Value.SplitIndex + 1, summaryRequest.Context.Count);
    }

    [Fact]
    public void TrimForPreview_DoesNotReportFalseEllipsisAfterWhitespaceCollapse() {
        var method = typeof(AgentEngine).GetMethod(
            "TrimForPreview",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        Assert.NotNull(method);

        var preview = (string)method!.Invoke(null, new object?[] { "alpha\n\nbeta", 96 })!;

        Assert.Equal("alpha beta", preview);
    }

    private static AgentEngine CreateEngine(params ITool[] tools) {
        var engine = new AgentEngine();

        if (tools is { Length: > 0 }) {
            foreach (var tool in tools) {
                if (tool is null) { continue; }
                engine.RegisterTool(tool);
            }
        }

        return engine;
    }

    private static RawToolCall CreateToolCallRequest(
        string toolName,
        string callId,
        IReadOnlyDictionary<string, string>? rawArguments,
        IReadOnlyDictionary<string, object?>? arguments = null
    ) {
        if (rawArguments is { Count: > 0 }) {
            var json = JsonSerializer.Serialize(rawArguments.ToDictionary(pair => pair.Key, pair => pair.Value));
            return new(toolName, callId, json);
        }

        if (arguments is { Count: > 0 }) {
            var json = JsonSerializer.Serialize(arguments);
            return new(toolName, callId, json);
        }

        return new(toolName, callId, "{}");
    }

    private static Action<CompletionAggregator>[] CreateDeltaSequence(params Action<CompletionAggregator>[] feeds) => feeds;

    private static LevelOfDetailContent UniformContent(string text)
        => new(text);

    private sealed class FakeProviderClient : ICompletionClient {
        private readonly Queue<Action<CompletionAggregator>[]> _responses;

        public string Name => "test-provider";
        public string ApiSpecId => "test-spec";

        public List<CompletionRequest> CapturedRequests { get; } = new();

        public FakeProviderClient(IEnumerable<Action<CompletionAggregator>[]> responses) {
            _responses = new Queue<Action<CompletionAggregator>[]>(responses ?? throw new ArgumentNullException(nameof(responses)));
        }

        public Task<CompletionResult> StreamCompletionAsync(CompletionRequest request, CompletionStreamObserver? observer, CancellationToken cancellationToken = default) {
            if (_responses.Count == 0) { throw new InvalidOperationException("No provider responses configured."); }

            CapturedRequests.Add(request);
            var feeds = _responses.Dequeue();
            var aggregator = new CompletionAggregator(CompletionDescriptor.From(this, request), observer);
            foreach (var feed in feeds) {
                feed(aggregator);
            }
            return Task.FromResult(aggregator.Build());
        }
    }

    private sealed class DelegateTool : ITool {
        private readonly Func<IReadOnlyDictionary<string, object?>?, LodToolExecuteResult> _execute;

        public DelegateTool(string name, Func<IReadOnlyDictionary<string, object?>?, LodToolExecuteResult> execute) {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        }

        public string Name { get; }

        public string Description => "delegate-tool";

        public IReadOnlyList<ToolParamSpec> Parameters { get; } = Array.Empty<ToolParamSpec>();

        public bool Visible { get; set; } = true;

        public ValueTask<LodToolExecuteResult> ExecuteAsync(IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken)
            => new(_execute(arguments));
    }

    private sealed class PrepareAwareApp : IApp {
        private readonly IReadOnlyList<ITool> _tools;

        public PrepareAwareApp(params ITool[] tools) {
            _tools = tools ?? Array.Empty<ITool>();
        }

        public string Name => "PrepareAware";

        public string Description => "Exposes mutable snapshot state for PrepareInvocationAsync tests.";

        public IReadOnlyList<ITool> Tools => _tools;

        public string Snapshot { get; set; } = string.Empty;

        public string? RenderWindow(AppRenderContext context) => $"PrepareWindow: {Snapshot}";
    }
}
