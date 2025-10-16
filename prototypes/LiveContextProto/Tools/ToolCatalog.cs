using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Atelia.LiveContextProto.Context;

namespace Atelia.LiveContextProto.Tools;

internal sealed class ToolCatalog {
    private readonly ImmutableDictionary<string, ITool> _tools;

    private ToolCatalog(ImmutableDictionary<string, ITool> tools) {
        _tools = tools;
    }

    public static ToolCatalog Create(IEnumerable<ITool> tools) {
        if (tools is null) { throw new ArgumentNullException(nameof(tools)); }

        var builder = ImmutableDictionary.CreateBuilder<string, ITool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools) {
            if (tool is null) { continue; }

            if (builder.ContainsKey(tool.Name)) { throw new InvalidOperationException($"Duplicate tool registration detected for '{tool.Name}'."); }

            builder[tool.Name] = tool;
        }

        return new ToolCatalog(builder.ToImmutable());
    }

    public IEnumerable<ITool> Tools => _tools.Values;

    public bool TryGet(string name, out ITool tool) {
        if (string.IsNullOrWhiteSpace(name)) {
            tool = null!;
            return false;
        }

        return _tools.TryGetValue(name, out tool!);
    }

    public ITool GetRequired(string name) {
        if (TryGet(name, out var tool)) { return tool; }
        throw new KeyNotFoundException($"Tool '{name}' is not registered.");
    }

    public IEnumerable<IToolHandler> CreateHandlers(Func<ITool, ImmutableDictionary<string, object?>>? environmentFactory = null) {
        foreach (var tool in _tools.Values) {
            yield return new ToolHandlerAdapter(tool, environmentFactory);
        }
    }

    private sealed class ToolHandlerAdapter : IToolHandler {
        private readonly ITool _tool;
        private readonly Func<ITool, ImmutableDictionary<string, object?>>? _environmentFactory;

        public ToolHandlerAdapter(ITool tool, Func<ITool, ImmutableDictionary<string, object?>>? environmentFactory) {
            _tool = tool;
            _environmentFactory = environmentFactory;
        }

        public string ToolName => _tool.Name;

        public ValueTask<ToolHandlerResult> ExecuteAsync(ToolCallRequest request, CancellationToken cancellationToken) {
            var normalizedRequest = EnsureArguments(request);
            var environment = _environmentFactory?.Invoke(_tool) ?? ImmutableDictionary<string, object?>.Empty;
            var context = new ToolExecutionContext(normalizedRequest, environment);
            return _tool.ExecuteAsync(context, cancellationToken);
        }

        private ToolCallRequest EnsureArguments(ToolCallRequest request) {
            if (request.Arguments is not null) { return request; }

            var parsed = ToolArgumentParser.ParseArguments(_tool, request.RawArguments);
            return request with {
                Arguments = parsed.Arguments,
                ParseError = CombineMessages(request.ParseError, parsed.ParseError),
                ParseWarning = CombineMessages(request.ParseWarning, parsed.ParseWarning)
            };
        }

        private static string? CombineMessages(string? primary, string? secondary) {
            if (string.IsNullOrWhiteSpace(primary)) { return secondary; }
            if (string.IsNullOrWhiteSpace(secondary)) { return primary; }
            return string.Concat(primary, "; ", secondary);
        }
    }
}
