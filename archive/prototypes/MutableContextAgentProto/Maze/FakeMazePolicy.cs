namespace Atelia.MutableContextAgentProto.Maze;

public sealed record FakeMazeDecision(
    bool IsComplete,
    string Thought,
    MazeToolIntent? ToolCall,
    string? Final
);

public sealed class FakeMazePolicy {
    public FakeMazeDecision Next(MazeWorld world) {
        if (world.IsAtGoal) {
            return new FakeMazeDecision(
                IsComplete: true,
                Thought: "Already at the goal.",
                ToolCall: null,
                Final: $"Goal reached at {world.Position} in {world.StepsTaken} steps."
            );
        }

        Direction? nextDirection = FindNextDirection(world);
        if (nextDirection is null) {
            return new FakeMazeDecision(
                IsComplete: false,
                Thought: "No path to the goal is currently reachable; inspect the maze state.",
                ToolCall: new MazeToolIntent(MazeToolFactory.StatusToolName, new Dictionary<string, string?>()),
                Final: null
            );
        }

        string directionToken = nextDirection.Value.ToToken();
        return new FakeMazeDecision(
            IsComplete: false,
            Thought: $"Take the shortest known path toward the goal: move {directionToken}.",
            ToolCall: new MazeToolIntent(
                MazeToolFactory.MoveToolName,
                new Dictionary<string, string?> { ["direction"] = directionToken }
            ),
            Final: null
        );
    }

    private static Direction? FindNextDirection(MazeWorld world) {
        var queue = new Queue<PathNode>();
        var visited = new HashSet<Position> { world.Position };

        foreach ((Direction direction, Position position) in world.GetOpenNeighbors(world.Position)) {
            queue.Enqueue(new PathNode(position, direction));
            visited.Add(position);
        }

        while (queue.Count > 0) {
            PathNode current = queue.Dequeue();
            if (current.Position == world.Goal) { return current.FirstDirection; }

            foreach ((_, Position next) in world.GetOpenNeighbors(current.Position)) {
                if (!visited.Add(next)) { continue; }

                queue.Enqueue(new PathNode(next, current.FirstDirection));
            }
        }

        return null;
    }

    private readonly record struct PathNode(Position Position, Direction FirstDirection);
}
