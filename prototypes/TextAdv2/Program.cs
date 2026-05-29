using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.StateJournal;
using Atelia.TextAdv2.ReadOnlyView;
using Atelia.TextAdv2.WorldTruth;

var command = ParseCommand(args);
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
	Console.WriteLine();

	string output = command.Mode switch {
		"world" => WorldDumpRenderer.Render(world),
		"location" => WorldDumpRenderer.RenderLocation(world, command.Id!),
		"observe-location" => JsonSerializer.Serialize(
			LocationObservationProjector.ObserveLocation(world, command.Id!),
			jsonOptions
		),
		"observe-actor" => JsonSerializer.Serialize(
			LocationObservationProjector.ObserveActorLocation(world, command.Id!),
			jsonOptions
		),
		_ => throw new InvalidOperationException($"Unsupported command mode '{command.Mode}'."),
	};

	Console.WriteLine(output);
}

static (string Mode, string? Id) ParseCommand(string[] args) {
	if (args.Length == 0) {
		return ("world", null);
	}

	if (args.Length == 2) {
		return args[0] switch {
			"--location" => ("location", args[1]),
			"--observe-location" => ("observe-location", args[1]),
			"--observe-actor" => ("observe-actor", args[1]),
			_ => throw new InvalidOperationException(BuildUsage()),
		};
	}

	throw new InvalidOperationException(BuildUsage());
}

static string BuildUsage()
	=> "Usage: dotnet run --project prototypes/TextAdv2/TextAdv2.csproj"
		+ " [--location <locationId> | --observe-location <locationId> | --observe-actor <actorId>]";
