using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Tools;

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

    public AppHostProjection Project(AppRenderContext context) {
        if (_apps.IsDefaultOrEmpty) {
            return new AppHostProjection(
                Windows: null,
                ToolAccessSnapshot: ToolAccessSnapshot.AllowAll
            );
        }

        var fragments = new List<string>();
        HashSet<string>? hiddenToolNames = null;

        foreach (var app in _apps) {
            var projection = app.Render(context);
            if (!string.IsNullOrWhiteSpace(projection.Window)) {
                fragments.Add(projection.Window.TrimEnd());
            }

            if (projection.HiddenToolNames is not { Count: > 0 }) { continue; }

            hiddenToolNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var toolName in projection.HiddenToolNames) {
                if (!string.IsNullOrWhiteSpace(toolName)) {
                    hiddenToolNames.Add(toolName);
                }
            }
        }

        string? windows = null;
        if (fragments.Count > 0) {
            var builder = new StringBuilder();
            builder.AppendLine("# [Window]");
            builder.AppendLine();

            for (var index = 0; index < fragments.Count; index++) {
                builder.AppendLine(fragments[index]);

                if (index < fragments.Count - 1) {
                    builder.AppendLine();
                }
            }

            windows = builder.ToString().TrimEnd();
        }

        return new AppHostProjection(
            Windows: windows,
            ToolAccessSnapshot: hiddenToolNames is null || hiddenToolNames.Count == 0
                ? ToolAccessSnapshot.AllowAll
                : new ToolAccessSnapshot(hiddenToolNames)
        );
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
