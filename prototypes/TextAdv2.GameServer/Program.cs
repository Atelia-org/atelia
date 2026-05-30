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
app.MapGet("/admin/world", (TextAdv2RuntimeService runtime) => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.World)));
app.MapGet("/admin/time", (TextAdv2RuntimeService runtime) => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveTime)));
app.MapPost("/admin/advance-time/{ticks}",
    (string ticks, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.AdvanceTime, ticks))
);
app.MapGet("/admin/route-acceleration",
    (TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveRouteAcceleration))
);
app.MapPost("/admin/route-acceleration/rebuild",
    (string? landmarks, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.RebuildRouteAcceleration, landmarks))
);
app.MapPost("/admin/reset-sample-world",
    (TextAdv2RuntimeService runtime) => {
        runtime.ResetToSampleWorld();
        return Results.Ok(BuildRuntimeStatus(runtime));
    }
);

app.MapGet("/admin/locations/{locationId}",
    (string locationId, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.Location, locationId))
);
app.MapGet("/admin/locations/{locationId}/observation",
    (string locationId, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveLocation, locationId))
);
app.MapGet("/admin/locations/{locationId}/navigation",
    (string locationId, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveNavigation, locationId))
);

app.MapGet("/actors/{actorId}/observation",
    (string actorId, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveActor, actorId))
);
app.MapGet("/actors/{actorId}/navigation",
    (string actorId, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.ObserveActorNavigation, actorId))
);
app.MapPost("/actors/{actorId}/moves/{passageId}",
    (string actorId, string passageId, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.MoveActor, actorId, passageId))
);
app.MapGet("/actors/{actorId}/route-trace",
    (string actorId, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.TraceActorRoute, actorId))
);
app.MapGet("/actors/{actorId}/plan-route/{toLocationId}",
    (string actorId, string toLocationId, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.PlanActorRoute, actorId, toLocationId))
);

app.MapGet("/admin/routes/{fromLocationId}/{toLocationId}",
    (string fromLocationId, string toLocationId, TextAdv2RuntimeService runtime)
    => Execute(runtime, new TextAdv2RuntimeCommand(TextAdv2RuntimeCommandMode.PlanRoute, fromLocationId, toLocationId))
);

app.Run();

static IResult Execute(TextAdv2RuntimeService runtime, TextAdv2RuntimeCommand command) {
    try {
        var result = runtime.Execute(command);
        return Results.Content(result.Output, result.ContentType);
    }
    catch (ArgumentException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex) {
        return Results.BadRequest(new { error = ex.Message });
    }
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
