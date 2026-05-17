namespace Atelia.MutableContextAgentProto.Maze;

public enum Direction {
    North,
    East,
    South,
    West,
}

public static class DirectionParser {
    public static bool TryParse(string? value, out Direction direction) {
        direction = Direction.North;

        if (string.IsNullOrWhiteSpace(value)) { return false; }

        switch (value.Trim().ToLowerInvariant()) {
            case "n":
            case "north":
            case "up":
                direction = Direction.North;
                return true;
            case "e":
            case "east":
            case "right":
                direction = Direction.East;
                return true;
            case "s":
            case "south":
            case "down":
                direction = Direction.South;
                return true;
            case "w":
            case "west":
            case "left":
                direction = Direction.West;
                return true;
            default:
                return false;
        }
    }

    public static string ToToken(this Direction direction) {
        return direction switch {
            Direction.North => "north",
            Direction.East => "east",
            Direction.South => "south",
            Direction.West => "west",
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
        };
    }

    public static Position Step(this Direction direction, Position origin) {
        return direction switch {
            Direction.North => origin with { Y = origin.Y - 1 },
            Direction.East => origin with { X = origin.X + 1 },
            Direction.South => origin with { Y = origin.Y + 1 },
            Direction.West => origin with { X = origin.X - 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
        };
    }
}
