using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Atelia.LiveContextProto.State;
using Atelia.LiveContextProto.Tools;

namespace Atelia.LiveContextProto.Apps;

internal sealed class DefaultAppHost : IAppHost {
    private readonly AgentState _state;
    private ImmutableDictionary<string, object?> _environment;
    private ImmutableArray<IApp> _apps;
    private ImmutableArray<ITool> _tools;

    public DefaultAppHost(
        AgentState state,
        IEnumerable<IApp>? apps = null,
        ImmutableDictionary<string, object?>? environment = null
    ) {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _environment = environment ?? ImmutableDictionary<string, object?>.Empty;
        _apps = ImmutableArray<IApp>.Empty;
        _tools = ImmutableArray<ITool>.Empty;

        if (apps is not null) {
            foreach (var app in apps) {
                RegisterApp(app);
            }
        }
    }

    public AgentState State => _state;

    public ImmutableArray<IApp> Apps => _apps;

    public ImmutableArray<ITool> Tools => _tools;

    public void RegisterApp(IApp app) {
        if (app is null) { throw new ArgumentNullException(nameof(app)); }

        var builder = _apps.ToBuilder();
        var existingIndex = FindAppIndex(app.Name);
        if (existingIndex >= 0) {
            builder[existingIndex] = app;
        }
        else {
            builder.Add(app);
        }

        _apps = builder.ToImmutable();
        RebuildToolCache();
    }

    public bool RemoveApp(string name) {
        if (string.IsNullOrWhiteSpace(name)) { return false; }

        var index = FindAppIndex(name);
        if (index < 0) { return false; }

        var builder = _apps.ToBuilder();
        builder.RemoveAt(index);
        _apps = builder.ToImmutable();
        RebuildToolCache();
        return true;
    }

    public string? RenderWindows() {
        if (_apps.IsDefaultOrEmpty) { return null; }

        var fragments = new List<string>();
        var renderContext = new AppRenderContext(_state, _environment);

        foreach (var app in _apps) {
            var fragment = app.RenderWindow(renderContext);
            if (!string.IsNullOrWhiteSpace(fragment)) {
                fragments.Add(fragment.TrimEnd());
            }
        }

        if (fragments.Count == 0) { return null; }

        var builder = new StringBuilder();
        builder.AppendLine("# [Window]");
        builder.AppendLine();

        for (var index = 0; index < fragments.Count; index++) {
            builder.AppendLine(fragments[index]);

            if (index < fragments.Count - 1) {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    public void UpdateEnvironment(ImmutableDictionary<string, object?> environment)
        => _environment = environment ?? ImmutableDictionary<string, object?>.Empty;

    private int FindAppIndex(string name) {
        if (_apps.IsDefaultOrEmpty) { return -1; }

        for (var index = 0; index < _apps.Length; index++) {
            if (string.Equals(_apps[index].Name, name, StringComparison.OrdinalIgnoreCase)) { return index; }
        }

        return -1;
    }

    private void RebuildToolCache() {
        if (_apps.IsDefaultOrEmpty) {
            _tools = ImmutableArray<ITool>.Empty;
            return;
        }

        var builder = ImmutableArray.CreateBuilder<ITool>();
        foreach (var app in _apps) {
            if (app.Tools is { Count: > 0 }) {
                builder.AddRange(app.Tools);
            }
        }

        _tools = builder.ToImmutable();
    }
}
