using System.Collections.ObjectModel;
using System.Text;

namespace Atelia.MutableContextAgentProto.Maze;

public readonly record struct Position(int X, int Y) {
    public override string ToString() => $"({X},{Y})";
}

public sealed record MazeStatus(
    Position Position,
    Position Goal,
    bool IsAtGoal,
    int StepsTaken,
    string Map
);

public sealed record MazeLookResult(
    Position Position,
    bool IsAtGoal,
    IReadOnlyDictionary<string, string> Exits,
    string Description,
    string Map
);

public sealed record MazeMoveResult(
    bool Success,
    string Message,
    Position Position,
    MazeStatus Status
);

public sealed class MazeWorld {
    private const char Wall = '#';
    private const char Open = '.';
    private const char Start = 'S';
    private const char GoalMarker = 'G';

    private static readonly string[] DefaultMap =
    [
        "########",
        "#S..#..#",
        "#.#.#G.#",
        "#.#....#",
        "#......#",
        "########",
    ];

    private readonly string[] _map;

    public MazeWorld()
        : this(DefaultMap) {
    }

    public MazeWorld(IReadOnlyList<string> map) {
        if (map.Count == 0) { throw new ArgumentException("Maze map must contain at least one row.", nameof(map)); }

        int width = map[0].Length;
        if (width == 0 || map.Any(row => row.Length != width)) { throw new ArgumentException("Maze map must be rectangular and non-empty.", nameof(map)); }

        _map = map.ToArray();
        Width = width;
        Height = _map.Length;
        StartPosition = FindSingle(Start);
        Goal = FindSingle(GoalMarker);
        Position = StartPosition;
    }

    public int Width { get; }

    public int Height { get; }

    public Position StartPosition { get; }

    public Position Goal { get; }

    public Position Position { get; private set; }

    public int StepsTaken { get; private set; }

    public bool IsAtGoal => Position == Goal;

    public MazeStatus Status() {
        return new MazeStatus(Position, Goal, IsAtGoal, StepsTaken, RenderMap());
    }

    public MazeLookResult Look() {
        var exits = GetOpenNeighbors(Position)
            .ToDictionary(
            item => item.Direction.ToToken(),
            item => DescribeCell(item.Position)
        );

        string description = IsAtGoal
            ? $"You are at {Position}. The goal is here."
            : $"You are at {Position}. The goal is at {Goal}. Open exits: {FormatExits(exits)}.";

        return new MazeLookResult(
            Position,
            IsAtGoal,
            new ReadOnlyDictionary<string, string>(exits),
            description,
            RenderMap()
        );
    }

    public MazeMoveResult Move(Direction direction) {
        Position next = direction.Step(Position);
        if (!IsInside(next)) { return FailedMove($"Cannot move {direction.ToToken()}: outside the maze."); }

        if (IsWall(next)) { return FailedMove($"Cannot move {direction.ToToken()}: a wall blocks the way."); }

        Position = next;
        StepsTaken++;

        string message = IsAtGoal
            ? $"Moved {direction.ToToken()} to {Position}. Goal reached."
            : $"Moved {direction.ToToken()} to {Position}.";

        return new MazeMoveResult(true, message, Position, Status());
    }

    public bool IsOpen(Position position) {
        return IsInside(position) && !IsWall(position);
    }

    public IEnumerable<(Direction Direction, Position Position)> GetOpenNeighbors(Position position) {
        foreach (Direction direction in DirectionOrder.All) {
            Position next = direction.Step(position);
            if (IsOpen(next)) {
                yield return (direction, next);
            }
        }
    }

    public string RenderMap() {
        var builder = new StringBuilder();
        for (int y = 0; y < Height; y++) {
            if (y > 0) {
                builder.AppendLine();
            }

            for (int x = 0; x < Width; x++) {
                var current = new Position(x, y);
                builder.Append(current == Position ? '@' : _map[y][x]);
            }
        }

        return builder.ToString();
    }

    private MazeMoveResult FailedMove(string message) {
        return new MazeMoveResult(false, message, Position, Status());
    }

    private Position FindSingle(char marker) {
        Position? found = null;
        for (int y = 0; y < Height; y++) {
            for (int x = 0; x < Width; x++) {
                if (_map[y][x] != marker) { continue; }

                if (found is not null) { throw new ArgumentException($"Maze map must contain exactly one '{marker}' marker."); }

                found = new Position(x, y);
            }
        }

        return found ?? throw new ArgumentException($"Maze map must contain exactly one '{marker}' marker.");
    }

    private bool IsInside(Position position) {
        return position.X >= 0 && position.X < Width && position.Y >= 0 && position.Y < Height;
    }

    private bool IsWall(Position position) {
        return _map[position.Y][position.X] == Wall;
    }

    private string DescribeCell(Position position) {
        return _map[position.Y][position.X] switch {
            GoalMarker => "goal",
            Start => "start",
            Open => "open path",
            _ => "open path",
        };
    }

    private static string FormatExits(IReadOnlyDictionary<string, string> exits) {
        return exits.Count == 0
            ? "none"
            : string.Join(", ", exits.Select(item => $"{item.Key} ({item.Value})"));
    }
}

internal static class DirectionOrder {
    public static readonly Direction[] All =
    [
        Direction.North,
        Direction.East,
        Direction.South,
        Direction.West,
    ];
}
