namespace Atelia.MutableContextAgentProto.Core;

public sealed class WorkingContext {
    private readonly List<ActionLogItem> _actionLog = [];
    private readonly List<MemoryItem> _memories = [];
    private readonly List<TransientView> _transientViews = [];
    private readonly List<ToolDescription> _availableTools = [];

    public WorkingContext(string initialGoal) {
        if (string.IsNullOrWhiteSpace(initialGoal)) { throw new ArgumentException("Initial goal must not be empty.", nameof(initialGoal)); }

        InitialGoal = initialGoal.Trim();
    }

    public string InitialGoal { get; set; }
    public IReadOnlyList<ActionLogItem> ActionLog => _actionLog;
    public IReadOnlyList<MemoryItem> Memories => _memories;
    public IReadOnlyList<TransientView> TransientViews => _transientViews;
    public IReadOnlyList<ToolDescription> AvailableTools => _availableTools;

    public ActionLogItem RecordAction(
        string title,
        string? detail = null,
        ActionStatus status = ActionStatus.Completed
    ) {
        var item = ActionLogItem.Create(title, detail, status);
        _actionLog.Add(item);
        return item;
    }

    public MemoryItem Remember(
        string content,
        MemoryKind kind = MemoryKind.Fact,
        string? source = null,
        string? key = null
    ) {
        var item = MemoryItem.Create(content, kind, source, key);
        _memories.Add(item);
        return item;
    }

    public MemoryItem UpsertMemory(
        string key,
        string content,
        MemoryKind kind = MemoryKind.Fact,
        string? source = null
    ) {
        if (string.IsNullOrWhiteSpace(key)) {
            throw new ArgumentException("Memory key must not be empty.", nameof(key));
        }

        var item = MemoryItem.Create(content, kind, source, key.Trim());
        var index = _memories.FindIndex(memory => string.Equals(memory.Key, item.Key, StringComparison.Ordinal));
        if (index < 0) {
            _memories.Add(item);
        }
        else {
            _memories[index] = item;
        }

        return item;
    }

    public TransientView AddTransientView(
        string id,
        string title,
        string content,
        string? source = null
    ) {
        var view = TransientView.Create(id, title, content, source);
        AddTransientView(view);
        return view;
    }

    public void AddTransientView(TransientView view) {
        ArgumentNullException.ThrowIfNull(view);

        RemoveTransientView(view.Id);
        _transientViews.Add(view);
    }

    public bool RemoveTransientView(string id) {
        var index = _transientViews.FindIndex(view => string.Equals(view.Id, id, StringComparison.Ordinal));
        if (index < 0) { return false; }

        _transientViews.RemoveAt(index);
        return true;
    }

    public ToolDescription AddTool(string name, string description, string? usage = null) {
        var tool = new ToolDescription(name, description, usage);
        _availableTools.Add(tool);
        return tool;
    }

    public bool RemoveTool(string name) {
        var index = _availableTools.FindIndex(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));
        if (index < 0) { return false; }

        _availableTools.RemoveAt(index);
        return true;
    }
}
