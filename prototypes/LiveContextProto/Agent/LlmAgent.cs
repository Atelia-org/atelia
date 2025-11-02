using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Apps;
using Atelia.LiveContextProto.Context;
using Atelia.LiveContextProto.Profile;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto.Agent;

internal sealed class LlmAgent {
    private readonly AgentEngine _engine;
    private readonly MemoryNotebookApp _memoryNotebookApp;
    private readonly ToolExecutor _toolExecutor;

    public LlmAgent(AgentState state, IEnumerable<ITool>? additionalTools = null) {
        if (state is null) { throw new ArgumentNullException(nameof(state)); }

        _memoryNotebookApp = new MemoryNotebookApp();
        state.ConfigureApps(new[] { _memoryNotebookApp });

        var toolList = state.EnumerateAppTools().ToList();
        if (additionalTools is not null) {
            toolList.AddRange(additionalTools);
        }

        _toolExecutor = new ToolExecutor(toolList);
        _engine = new AgentEngine(state, _toolExecutor, _memoryNotebookApp);
    }

    public AgentEngine Engine => _engine;

    public ToolExecutor ToolExecutor => _toolExecutor;

    public AgentState State => _engine.State;

    public string SystemInstruction => _engine.SystemInstruction;

    public string MemoryNotebookSnapshot => _engine.MemoryNotebookSnapshot;

    public IReadOnlyList<IContextMessage> RenderLiveContext() => _engine.RenderLiveContext();

    public void Reset() => _engine.Reset();

    public void UpdateMemoryNotebook(string? content) => _engine.UpdateMemoryNotebook(content);

    public void AppendNotification(LevelOfDetailContent notificationContent) => _engine.AppendNotification(notificationContent);

    public void AppendNotification(string basic, string? detail = null) => _engine.AppendNotification(basic, detail);

    public Task<AgentStepResult> DoStepAsync(LlmProfile profile, CancellationToken cancellationToken = default)
        => _engine.StepAsync(profile, cancellationToken);

    public void RefreshToolDefinitions() => _engine.RefreshToolDefinitions();

    public event EventHandler<WaitingInputEventArgs>? WaitingInput {
        add => _engine.WaitingInput += value;
        remove => _engine.WaitingInput -= value;
    }

    public event EventHandler<BeforeModelCallEventArgs>? BeforeModelCall {
        add => _engine.BeforeModelCall += value;
        remove => _engine.BeforeModelCall -= value;
    }

    public event EventHandler<AfterModelCallEventArgs>? AfterModelCall {
        add => _engine.AfterModelCall += value;
        remove => _engine.AfterModelCall -= value;
    }

    public event EventHandler<BeforeToolExecuteEventArgs>? BeforeToolExecute {
        add => _engine.BeforeToolExecute += value;
        remove => _engine.BeforeToolExecute -= value;
    }

    public event EventHandler<AfterToolExecuteEventArgs>? AfterToolExecute {
        add => _engine.AfterToolExecute += value;
        remove => _engine.AfterToolExecute -= value;
    }

    public event EventHandler<StateTransitionEventArgs>? StateTransition {
        add => _engine.StateTransition += value;
        remove => _engine.StateTransition -= value;
    }
}
