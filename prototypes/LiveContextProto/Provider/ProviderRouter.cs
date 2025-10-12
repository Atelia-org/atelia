using System;
using System.Collections.Generic;
using Atelia.Diagnostics;
using Atelia.LiveContextProto.State.History;

namespace Atelia.LiveContextProto.Provider;

internal sealed record ProviderRouteDefinition(
    string StrategyId,
    string ProviderId,
    string Specification,
    string Model,
    IProviderClient Client,
    string? DefaultStubScriptName
);

internal sealed class ProviderRouter {
    public const string DefaultStubStrategy = "stub/script";

    private readonly Dictionary<string, ProviderRouteDefinition> _routes;

    public ProviderRouter(IEnumerable<ProviderRouteDefinition> routes) {
        _routes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes) {
            _routes[route.StrategyId] = route;
        }

        DebugUtil.Print("Provider", $"ProviderRouter initialized with routes={_routes.Count}");
    }

    public ProviderInvocationPlan Resolve(ProviderInvocationOptions options) {
        if (!_routes.TryGetValue(options.StrategyId, out var definition)) { throw new InvalidOperationException($"Unknown provider strategy '{options.StrategyId}'."); }

        var script = options.StubScriptName ?? definition.DefaultStubScriptName;
        var invocation = new ModelInvocationDescriptor(definition.ProviderId, definition.Specification, definition.Model);

        DebugUtil.Print("Provider", $"[Router] Strategy={options.StrategyId} resolved to provider={invocation.ProviderId}, spec={invocation.Specification}, model={invocation.Model}, script={script ?? "(default-null)"}");

        return new ProviderInvocationPlan(definition.StrategyId, definition.Client, invocation, script);
    }

    public static ProviderRouter CreateDefault(IProviderClient stubProvider)
        => new(new[] {
            new ProviderRouteDefinition(
                DefaultStubStrategy,
                ProviderId: "stub",
                Specification: "script/default",
                Model: "livecontextproto-stub",
                Client: stubProvider,
                DefaultStubScriptName: "default"
            )
        });
}
