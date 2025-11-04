using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Atelia.Agent.Core;
using Atelia.Agent.Apps;
using Atelia.Completion.Abstractions;
using Atelia.Agent.Core.App;
using Atelia.Agent.Core.History;
using Atelia.Agent.Core.Tool;

namespace Atelia.Agent;

public sealed class CharacterAgent {
    private readonly AgentEngine _engine;
    private readonly MemoryNotebookApp _memoryNotebookApp;

    public CharacterAgent(IEnumerable<ITool>? additionalTools = null)
        : this(null, additionalTools) { }

    public CharacterAgent(AgentState? initialState, IEnumerable<ITool>? additionalTools = null) {
        _engine = new AgentEngine(initialState);

        _memoryNotebookApp = new MemoryNotebookApp();
        _engine.RegisterApp(_memoryNotebookApp);

        if (additionalTools is not null) {
            foreach (var tool in additionalTools) {
                if (tool is null) { continue; }
                _engine.RegisterTool(tool);
            }
        }
    }

    public AgentState State => _engine.State;

    public string SystemInstruction => _engine.SystemInstruction;

    public string MemoryNotebookSnapshot => _memoryNotebookApp.GetSnapshot();

    public void RegisterApp(IApp app) => _engine.RegisterApp(app);

    public bool RemoveApp(string name) => _engine.RemoveApp(name);

    public void RegisterTool(ITool tool) => _engine.RegisterTool(tool);

    public bool RemoveTool(string name) => _engine.RemoveTool(name);

    public void UpdateMemoryNotebook(string? content)
        => _memoryNotebookApp.ReplaceNotebookFromHost(content);

    public void AppendNotification(LevelOfDetailContent notificationContent) => _engine.AppendNotification(notificationContent);

    public void AppendNotification(string basic, string? detail = null) => _engine.AppendNotification(basic, detail);

    public Task<AgentStepResult> DoStepAsync(LlmProfile profile, CancellationToken cancellationToken = default)
        => _engine.StepAsync(profile, cancellationToken);
}
