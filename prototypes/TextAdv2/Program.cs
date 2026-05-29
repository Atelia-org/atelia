using Atelia.StateJournal;
using Atelia.TextAdv2.WorldTruth;

var repoDir = Path.Combine(Path.GetTempPath(), $"atelia-textadv2-{Guid.NewGuid():N}");

using (var repo = Repository.Create(repoDir).Unwrap()) {
	var revision = repo.CreateBranch("main").Unwrap();
	var world = WorldState.Create(revision);

	var beach = world.CreateLocation(
		"beach",
		"Beach",
		"潮湿的海滩延伸到灰色天际，沙面上还有昨夜雨水留下的暗痕。"
	);
	var forest = world.CreateLocation(
		"forest",
		"Forest",
		"林间土路被雨水泡软，树冠还在往下滴水。"
	);

	var coastRoad = world.CreatePassage(
		"beach-forest-road",
		beach.Id,
		"north",
		forest.Id,
		"south",
		TravelMode.Land,
		baseTravelCost: 2
	);
	coastRoad.SharedConditionNote = "雨后的泥路让整段行程都更滑。";
	coastRoad.GetDirectionFrom(beach.Id).TravelCostModifier = 1;
	coastRoad.GetDirectionFrom(beach.Id).DirectionConditionNote = "从海滩往林地走是缓上坡。";
	coastRoad.GetDirectionFrom(forest.Id).DirectionConditionNote = "回到海滩时更省力，但转弯处仍旧湿滑。";

	repo.Commit(world.Root).Unwrap();
}

using (var repo = Repository.Open(repoDir).Unwrap()) {
	var revision = repo.CheckoutBranch("main").Unwrap();
	var world = WorldState.FromRoot(revision.GetGraphRoot<DurableDict<string>>().Unwrap());
	var beach = world.GetLocation("beach");

	Console.WriteLine($"TextAdv2 world repo: {repoDir}");
	Console.WriteLine($"{beach.Name} passages:");

	foreach (var passage in world.EnumeratePassagesTouching(beach.Id)) {
		var exit = passage.GetEndpointFor(beach.Id);
		var destination = world.GetLocation(passage.GetOtherLocationId(beach.Id));
		var direction = passage.GetDirectionFrom(beach.Id);

		Console.WriteLine(
			$"- {exit.ExitName} -> {destination.Name} | mode={passage.TravelMode} | baseCost={passage.BaseTravelCost} | modifier={direction.TravelCostModifier}"
		);

		if (!string.IsNullOrEmpty(passage.SharedConditionNote)) {
			Console.WriteLine($"  shared: {passage.SharedConditionNote}");
		}
		if (!string.IsNullOrEmpty(direction.DirectionConditionNote)) {
			Console.WriteLine($"  directional: {direction.DirectionConditionNote}");
		}
	}
}
