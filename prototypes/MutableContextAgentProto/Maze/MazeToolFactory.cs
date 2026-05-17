namespace Atelia.MutableContextAgentProto.Maze;

public sealed record MazeToolParameter(string Name, string Description, bool Required);

public sealed record MazeToolSpec(
    string Name,
    string Description,
    IReadOnlyList<MazeToolParameter> Parameters
);

public sealed record MazeToolIntent(
    string Name,
    IReadOnlyDictionary<string, string?> Arguments
);

public sealed record MazeToolResult(
    string Name,
    bool Success,
    string Message,
    object Payload
);

public sealed class MazeToolFactory {
    public const string LookToolName = "maze.look";
    public const string MoveToolName = "maze.move";
    public const string StatusToolName = "maze.status";

    private static readonly MazeToolSpec[] ToolSpecs =
    [
        new(
            LookToolName,
            "Inspect the current maze position and available exits.",
            []),
        new(
            MoveToolName,
            "Move one step in a cardinal direction.",
            [new MazeToolParameter("direction", "north, east, south, or west", Required: true)]),
        new(
            StatusToolName,
            "Return the current position, goal, step count, and rendered map.",
            []),
    ];

    public MazeToolFactory(MazeWorld world) {
        World = world;
    }

    public MazeWorld World { get; }

    public static MazeWorld CreateDefaultWorld() => new();

    public static IReadOnlyList<MazeToolSpec> CreateToolSpecs() => ToolSpecs;

    public MazeToolResult Execute(MazeToolIntent intent) {
        return Execute(intent.Name, intent.Arguments);
    }

    public MazeToolResult Execute(
        string toolName,
        IReadOnlyDictionary<string, string?>? arguments = null
    ) {
        return toolName switch {
            LookToolName => Look(),
            MoveToolName => Move(arguments),
            StatusToolName => Status(),
            _ => new MazeToolResult(toolName, false, $"Unknown maze tool '{toolName}'.", new { }),
        };
    }

    private MazeToolResult Look() {
        MazeLookResult result = World.Look();
        return new MazeToolResult(LookToolName, true, result.Description, result);
    }

    private MazeToolResult Status() {
        MazeStatus status = World.Status();
        string message = status.IsAtGoal
            ? $"At {status.Position}; goal reached in {status.StepsTaken} steps."
            : $"At {status.Position}; goal is {status.Goal}; steps taken: {status.StepsTaken}.";

        return new MazeToolResult(StatusToolName, true, message, status);
    }

    private MazeToolResult Move(IReadOnlyDictionary<string, string?>? arguments) {
        if (arguments is null || !arguments.TryGetValue("direction", out string? rawDirection)) {
            return new MazeToolResult(
                MoveToolName,
                false,
                "Missing required argument 'direction'.",
                World.Status()
            );
        }

        if (!DirectionParser.TryParse(rawDirection, out Direction direction)) {
            return new MazeToolResult(
                MoveToolName,
                false,
                $"Invalid direction '{rawDirection}'. Expected north, east, south, or west.",
                World.Status()
            );
        }

        MazeMoveResult result = World.Move(direction);
        return new MazeToolResult(MoveToolName, result.Success, result.Message, result);
    }
}
