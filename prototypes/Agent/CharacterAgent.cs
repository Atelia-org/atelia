using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Agent.Core;
using Atelia.Agent.Apps;
using Atelia.Completion.Abstractions;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.App;
using Atelia.Agent.Core.Tool;

namespace Atelia.Agent;

public sealed class CharacterAgent {
    private readonly AgentEngine _engine;
    private readonly ToolExecutor _toolExecutor;
    private readonly IAppHost _appHost;
    private readonly MemoryNotebookApp? _memoryNotebookApp;

    public CharacterAgent(AgentState state, IEnumerable<ITool>? additionalTools = null) {
        if (state is null) { throw new ArgumentNullException(nameof(state)); }

        _appHost = new DefaultAppHost(state);

        var memoryNotebookApp = new MemoryNotebookApp();
        _appHost.RegisterApp(memoryNotebookApp);
        _memoryNotebookApp = memoryNotebookApp;

        var toolList = new List<ITool>(_appHost.Tools);
        if (additionalTools is not null) {
            toolList.AddRange(additionalTools);
        }

        _toolExecutor = new ToolExecutor(toolList);
        _engine = new AgentEngine(state, _toolExecutor, _appHost);
    }

    public AgentEngine Engine => _engine;

    public ToolExecutor ToolExecutor => _toolExecutor;

    public IAppHost AppHost => _appHost;

    public AgentState State => _engine.State;

    public string SystemInstruction => _engine.SystemInstruction;

    public string MemoryNotebookSnapshot
        => _memoryNotebookApp?.GetSnapshot() ?? MemoryNotebookApp.DefaultSnapshot;

    public IReadOnlyList<IHistoryMessage> RenderLiveContext() => _engine.RenderLiveContext();

    public void UpdateMemoryNotebook(string? content) {
        if (_memoryNotebookApp is null) { throw new InvalidOperationException("Memory notebook app is not configured for this agent."); }

        _memoryNotebookApp.ReplaceNotebookFromHost(content);
    }

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
