using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

var commands = ParseCommands(args);
var movementHistoryByActor = new Dictionary<string, List<ActorMovementObservation>>(StringComparer.Ordinal);
var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-textadv2-{Guid.NewGuid():N}");
var jsonOptions = new JsonSerializerOptions {
	WriteIndented = true,
};
jsonOptions.Converters.Add(new JsonStringEnumConverter());

using (var repo = Repository.Create(repoDir).Unwrap()) {
	var revision = repo.CreateBranch("main").Unwrap();
	var world = TestWorldBuilder.Create(revision);
	TestWorldBuilder.PopulateSampleActors(world);

	repo.Commit(world.Root).Unwrap();
}

using (var repo = Repository.Open(repoDir).Unwrap()) {
	var revision = repo.CheckoutBranch("main").Unwrap();
	var world = WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap());

	Console.WriteLine($"TextAdv2 world repo: {repoDir}");

	for (int i = 0; i < commands.Length; i++) {
		var command = commands[i];
		string output = command.Mode switch {
			"world" => WorldDumpRenderer.Render(world),
			"location" => WorldDumpRenderer.RenderLocation(world, command.Arg1!),
			"observe-location" => JsonSerializer.Serialize(
				LocationObservationProjector.ObserveLocation(world, command.Arg1!),
				jsonOptions
			),
			"observe-actor" => JsonSerializer.Serialize(
				LocationObservationProjector.ObserveActorLocation(world, command.Arg1!),
				jsonOptions
			),
			"observe-navigation" => JsonSerializer.Serialize(
				NavigationObservationProjector.ObserveLocationNavigation(world, command.Arg1!),
				jsonOptions
			),
			"observe-actor-navigation" => JsonSerializer.Serialize(
				NavigationObservationProjector.ObserveActorNavigation(world, command.Arg1!),
				jsonOptions
			),
			"plan-actor-route" => LocationRoutePlanTextRenderer.Render(
				LocationRoutePlanner.PlanShortestRouteForActor(world, command.Arg1!, command.Arg2!)
			),
			"plan-route" => LocationRoutePlanTextRenderer.Render(
				LocationRoutePlanner.PlanShortestRoute(world, command.Arg1!, command.Arg2!)
			),
			"trace-actor-route" => ActorRouteTraceTextRenderer.Render(
				ActorRouteTraceProjector.ObserveActorRouteTrace(
					world,
					command.Arg1!,
					GetMovementHistory(movementHistoryByActor, command.Arg1!)
				)
			),
			"move-actor-quiet" => RenderCompactMovement(
				ExecuteMoveActor(world, repo, movementHistoryByActor, command.Arg1!, command.Arg2!)
			),
			"move-actor" => JsonSerializer.Serialize(
				ExecuteMoveActor(world, repo, movementHistoryByActor, command.Arg1!, command.Arg2!),
				jsonOptions
			),
			_ => throw new InvalidOperationException($"Unsupported command mode '{command.Mode}'."),
		};

		Console.WriteLine();
		if (commands.Length > 1) {
			Console.WriteLine($"[{i + 1}/{commands.Length}] {DescribeCommand(command)}");
		}
		Console.WriteLine(output);
	}
}

static CommandSpec[] ParseCommands(string[] args) {
	if (args.Length == 0) {
		return [new("world", null, null)];
	}

	var commands = new List<CommandSpec>();
	int index = 0;
	while (index < args.Length) {
		switch (args[index]) {
			case "--world":
				commands.Add(new("world", null, null));
				index += 1;
				break;
			case "--location":
				commands.Add(new("location", RequireArg(args, index + 1), null));
				index += 2;
				break;
			case "--observe-location":
				commands.Add(new("observe-location", RequireArg(args, index + 1), null));
				index += 2;
				break;
			case "--observe-actor":
				commands.Add(new("observe-actor", RequireArg(args, index + 1), null));
				index += 2;
				break;
			case "--observe-navigation":
				commands.Add(new("observe-navigation", RequireArg(args, index + 1), null));
				index += 2;
				break;
			case "--observe-actor-navigation":
				commands.Add(new("observe-actor-navigation", RequireArg(args, index + 1), null));
				index += 2;
				break;
			case "--plan-actor-route":
				commands.Add(new("plan-actor-route", RequireArg(args, index + 1), RequireArg(args, index + 2)));
				index += 3;
				break;
			case "--plan-route":
				commands.Add(new("plan-route", RequireArg(args, index + 1), RequireArg(args, index + 2)));
				index += 3;
				break;
			case "--trace-actor-route":
				commands.Add(new("trace-actor-route", RequireArg(args, index + 1), null));
				index += 2;
				break;
			case "--move-actor-quiet":
				commands.Add(new("move-actor-quiet", RequireArg(args, index + 1), RequireArg(args, index + 2)));
				index += 3;
				break;
			case "--move-actor":
				commands.Add(new("move-actor", RequireArg(args, index + 1), RequireArg(args, index + 2)));
				index += 3;
				break;
			default:
				throw new InvalidOperationException(BuildUsage());
		}
	}

	return commands.ToArray();
}

static string RequireArg(string[] args, int index)
	=> index < args.Length ? args[index] : throw new InvalidOperationException(BuildUsage());

static string DescribeCommand(CommandSpec command)
	=> command.Mode switch {
		"world" => "world dump",
		"location" => $"location dump {command.Arg1}",
		"observe-location" => $"observe location {command.Arg1}",
		"observe-actor" => $"observe actor {command.Arg1}",
		"observe-navigation" => $"observe navigation {command.Arg1}",
		"observe-actor-navigation" => $"observe actor navigation {command.Arg1}",
		"plan-actor-route" => $"plan actor route {command.Arg1} -> {command.Arg2}",
		"plan-route" => $"plan route {command.Arg1} -> {command.Arg2}",
		"trace-actor-route" => $"trace actor route {command.Arg1}",
		"move-actor-quiet" => $"move actor quietly {command.Arg1} via {command.Arg2}",
		"move-actor" => $"move actor {command.Arg1} via {command.Arg2}",
		_ => command.Mode,
	};

static ActorMovementObservation ExecuteMoveActor(
	WorldState world,
	Repository repo,
	Dictionary<string, List<ActorMovementObservation>> movementHistoryByActor,
	string actorId,
	string passageId
) {
	var actor = world.GetActor(actorId);
	var fromLocation = world.GetLocation(actor.CurrentLocationId);
	var passage = world.GetPassage(passageId);
	var exit = passage.GetEndpointFor(fromLocation.Id);
	var direction = passage.GetDirectionFrom(fromLocation.Id);
	var toLocation = world.GetLocation(passage.GetOtherLocationId(fromLocation.Id));

	world.MoveActorAlongPassage(actorId, passageId);
	repo.Commit(world.Root).Unwrap();

	var currentObservation = LocationObservationProjector.ObserveActorLocation(world, actorId);
	var movement = new ActorMovementObservation(
		currentObservation.ActorId,
		currentObservation.ActorName,
		passage.Id,
		exit.ExitName,
		fromLocation.Id,
		fromLocation.Name,
		toLocation.Id,
		toLocation.Name,
		passage.TravelMode,
		direction.TotalTravelCost(passage),
		currentObservation.Location
	);
	GetOrCreateMovementHistory(movementHistoryByActor, actorId).Add(movement);
	return movement;
}

static IReadOnlyList<ActorMovementObservation> GetMovementHistory(
	Dictionary<string, List<ActorMovementObservation>> movementHistoryByActor,
	string actorId
) => movementHistoryByActor.TryGetValue(actorId, out var history) ? history : [];

static List<ActorMovementObservation> GetOrCreateMovementHistory(
	Dictionary<string, List<ActorMovementObservation>> movementHistoryByActor,
	string actorId
) {
	if (!movementHistoryByActor.TryGetValue(actorId, out var history)) {
		history = [];
		movementHistoryByActor.Add(actorId, history);
	}

	return history;
}

static string RenderCompactMovement(ActorMovementObservation movement)
	=> $"{movement.ActorId}: {movement.FromLocationId} --{movement.ExitName}/{movement.PassageId}--> {movement.ToLocationId}"
		+ $" | {movement.TravelMode.ToStorageValue()} | cost={movement.TravelCost}";

static string BuildUsage()
	=> "Usage: dotnet run --project prototypes/TextAdv2/TextAdv2.csproj"
		+ " [--world]"
		+ " [--location <locationId>]"
		+ " [--observe-location <locationId>]"
		+ " [--observe-actor <actorId>]"
		+ " [--observe-navigation <locationId>]"
		+ " [--observe-actor-navigation <actorId>]"
		+ " [--plan-actor-route <actorId> <toLocationId>]"
		+ " [--plan-route <fromLocationId> <toLocationId>]"
		+ " [--trace-actor-route <actorId>]"
		+ " [--move-actor-quiet <actorId> <passageId>]"
		+ " [--move-actor <actorId> <passageId>]";

sealed record CommandSpec(string Mode, string? Arg1, string? Arg2);
