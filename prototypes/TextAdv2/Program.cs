using Atelia.StateJournal;
using Atelia.TextAdv2.WorldTruth;

string? locationFilter = ParseLocationFilter(args);
var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-textadv2-{Guid.NewGuid():N}");

using (var repo = Repository.Create(repoDir).Unwrap()) {
	var revision = repo.CreateBranch("main").Unwrap();
	var world = TestWorldBuilder.Create(revision);

	repo.Commit(world.Root).Unwrap();
}

using (var repo = Repository.Open(repoDir).Unwrap()) {
	var revision = repo.CheckoutBranch("main").Unwrap();
	var world = WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap());

	Console.WriteLine($"TextAdv2 world repo: {repoDir}");
	Console.WriteLine();
	Console.WriteLine(
		locationFilter is null
			? WorldDumpRenderer.Render(world)
			: WorldDumpRenderer.RenderLocation(world, locationFilter)
	);
}

static string? ParseLocationFilter(string[] args) {
	if (args.Length == 0) {
		return null;
	}

	if (args.Length == 2 && string.Equals(args[0], "--location", StringComparison.Ordinal)) {
		return args[1];
	}

	throw new InvalidOperationException("Usage: dotnet run --project prototypes/TextAdv2/TextAdv2.csproj [--location <locationId>]");
}
