using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Atelia.Agent.Core.Tool;

namespace Atelia.Agent.Core.App;

internal sealed class DefaultAppHost : IAppHost {
    private ImmutableArray<IApp> _apps;
    private ImmutableArray<ITool> _tools;

    public DefaultAppHost(
        IEnumerable<IApp>? apps = null
    ) {
        _apps = ImmutableArray<IApp>.Empty;
        _tools = ImmutableArray<ITool>.Empty;

        if (apps is not null) {
            foreach (var app in apps) {
                RegisterApp(app);
            }
        }
    }

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

        foreach (var app in _apps) {
            var fragment = app.RenderWindow();
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
