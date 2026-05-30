using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.TextAdv2.GameServer;
using Atelia.TextAdv2.Runtime;

var builder = WebApplication.CreateBuilder(args);

var configuredRepoDir = builder.Configuration["TextAdv2:RepoDir"] ?? ".atelia/textadv2-dev-world";
string resolvedRepoDir = Path.GetFullPath(configuredRepoDir, builder.Environment.ContentRootPath);
bool autoBootstrapSampleWorld = builder.Configuration.GetValue("TextAdv2:AutoBootstrapSampleWorld", true);

builder.Services.AddSingleton(_ => new TextAdv2RuntimeService(resolvedRepoDir, autoBootstrapSampleWorld));

var app = builder.Build();

app.MapGet("/", (TextAdv2RuntimeService runtime) => Results.Ok(BuildRuntimeStatus(runtime)));
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", host = "TextAdv2.GameServer", mode = "runtime-connected" }));
app.MapGet("/admin/runtime-status", (TextAdv2RuntimeService runtime) => Results.Ok(BuildRuntimeStatus(runtime)));
app.MapGet("/admin/world", (TextAdv2RuntimeService runtime) => Execute(runtime, static x => x.DumpWorld()));
app.MapGet("/admin/time", (TextAdv2RuntimeService runtime) => ExecuteJson(runtime, static x => x.ObserveTime()));
app.MapPost("/admin/advance-time/{ticks}",
    (string ticks, TextAdv2RuntimeService runtime)
    => ExecuteJson(runtime, x => x.AdvanceTime(ParseNonNegativeTickDelta(ticks)))
);
app.MapGet("/admin/route-acceleration",
    (TextAdv2RuntimeService runtime)
    => ExecuteJson(runtime, static x => x.ObserveRouteAcceleration())
);
app.MapPost("/admin/route-acceleration/rebuild",
    (string? landmarks, TextAdv2RuntimeService runtime)
    => ExecuteJson(runtime, x => TextAdv2SampleWorldDevBootstrap.RebuildRouteAcceleration(x, landmarks))
);
app.MapPost("/admin/reset-sample-world",
    (TextAdv2RuntimeService runtime) => {
        runtime.ResetToSampleWorld();
        return Results.Ok(BuildRuntimeStatus(runtime));
    }
);

app.MapGet("/admin/locations/{locationId}",
    (string locationId, TextAdv2RuntimeService runtime)
    => Execute(runtime, x => x.DumpLocation(locationId))
);
app.MapGet("/admin/locations/{locationId}/observation",
    (string locationId, TextAdv2RuntimeService runtime)
    => ExecuteJson(runtime, x => x.ObserveLocation(locationId))
);
app.MapGet("/admin/locations/{locationId}/navigation",
    (string locationId, TextAdv2RuntimeService runtime)
    => ExecuteJson(runtime, x => x.ObserveNavigation(locationId))
);

app.MapGet("/actors/{actorId}/observation",
    (string actorId, TextAdv2RuntimeService runtime)
    => ExecuteJson(runtime, x => x.ObserveActor(actorId))
);
app.MapGet("/actors/{actorId}/navigation",
    (string actorId, TextAdv2RuntimeService runtime)
    => ExecuteJson(runtime, x => x.ObserveActorNavigation(actorId))
);
app.MapPost("/actors/{actorId}/moves/{passageId}",
    (string actorId, string passageId, TextAdv2RuntimeService runtime)
    => ExecuteJson(runtime, x => x.MoveActor(actorId, passageId))
);
app.MapGet("/actors/{actorId}/route-trace",
    (string actorId, TextAdv2RuntimeService runtime)
    => Execute(runtime, x => x.TraceActorRoute(actorId))
);
app.MapGet("/actors/{actorId}/plan-route/{toLocationId}",
    (string actorId, string toLocationId, TextAdv2RuntimeService runtime)
    => Execute(runtime, x => x.PlanActorRoute(actorId, toLocationId))
);

app.MapGet("/admin/routes/{fromLocationId}/{toLocationId}",
    (string fromLocationId, string toLocationId, TextAdv2RuntimeService runtime)
    => Execute(runtime, x => x.PlanRoute(fromLocationId, toLocationId))
);

app.Run();

static IResult Execute(TextAdv2RuntimeService runtime, Func<TextAdv2Runtime, TextAdv2RuntimeCommandResult> operation) {
    try {
        var result = runtime.Invoke(operation);
        return Results.Content(result.Output, result.ContentType);
    }
    catch (ArgumentException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
}

static IResult ExecuteJson<T>(TextAdv2RuntimeService runtime, Func<TextAdv2Runtime, T> operation) {
    try {
        var result = runtime.Invoke(operation);
        return Results.Content(JsonSerializer.Serialize(result, TextAdv2HostJson.Options), TextAdv2RuntimeContentTypes.Json);
    }
    catch (ArgumentException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
}

static long ParseNonNegativeTickDelta(string value) {
    if (!long.TryParse(value, out long ticks)) { throw new InvalidOperationException($"AdvanceTime requires an integer tick delta, but received '{value}'."); }

    ArgumentOutOfRangeException.ThrowIfNegative(ticks);
    return ticks;
}

object BuildRuntimeStatus(TextAdv2RuntimeService runtime) {
    var scaffold = TextAdv2RuntimeScaffold.DescribeCurrentState();
    return new {
        service = "TextAdv2.GameServer",
        mode = "runtime-connected",
        configuration = new {
            configuredRepoDir,
            resolvedRepoDir = runtime.RepoDir,
            autoBootstrapSampleWorld,
        },
        runtime = scaffold,
        plannedEndpoints = new[] {
            "GET /admin/world",
            "GET /admin/time",
            "POST /admin/advance-time/{ticks}",
            "GET /admin/route-acceleration",
            "POST /admin/route-acceleration/rebuild?landmarks=<locationId[,locationId...]|default>",
            "POST /admin/reset-sample-world",
            "GET /admin/locations/{locationId}",
            "GET /admin/locations/{locationId}/observation",
            "GET /admin/locations/{locationId}/navigation",
            "GET /admin/routes/{fromLocationId}/{toLocationId}",
            "GET /actors/{actorId}/observation",
            "GET /actors/{actorId}/navigation",
            "POST /actors/{actorId}/moves/{passageId}",
            "GET /actors/{actorId}/route-trace",
            "GET /actors/{actorId}/plan-route/{toLocationId}",
        },
    };
}

public partial class Program {
}

file static class TextAdv2HostJson {
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions() {
        var options = new JsonSerializerOptions {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
