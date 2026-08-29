namespace Atelia.MemoPod.DebugApp;

internal sealed class OperatorArguments {
    private readonly Dictionary<string, List<string>> _values;

    private OperatorArguments(
        string command,
        Dictionary<string, List<string>> values
    ) {
        Command = command;
        _values = values;
    }

    internal string Command { get; }

    internal static OperatorArguments Parse(string[] args) {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0
            || string.IsNullOrWhiteSpace(args[0])
            || args[0].StartsWith("--", StringComparison.Ordinal)) {
            throw new OperatorSyntaxException();
        }

        var values = new Dictionary<string, List<string>>(
            StringComparer.Ordinal
        );
        for (int index = 1; index < args.Length; index += 2) {
            string option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal)
                || option.Length == 2
                || option.Contains('=', StringComparison.Ordinal)
                || index + 1 >= args.Length
                || args[index + 1].StartsWith(
                    "--",
                    StringComparison.Ordinal
                )) {
                throw new OperatorSyntaxException();
            }

            string key = option[2..];
            if (!values.TryGetValue(key, out List<string>? occurrences)) {
                occurrences = [];
                values.Add(key, occurrences);
            }
            occurrences.Add(args[index + 1]);
        }
        return new OperatorArguments(args[0], values);
    }

    internal void RequireShape(
        IReadOnlySet<string> singleValueKeys,
        IReadOnlySet<string> repeatedValueKeys
    ) {
        foreach ((string key, List<string> occurrences) in _values) {
            if (repeatedValueKeys.Contains(key)) { continue; }
            if (!singleValueKeys.Contains(key) || occurrences.Count != 1) {
                throw new OperatorSyntaxException();
            }
        }
    }

    internal string RequireSingle(string key) {
        if (!_values.TryGetValue(key, out List<string>? values)
            || values.Count != 1) {
            throw new OperatorSyntaxException();
        }
        return values[0];
    }

    internal bool Contains(string key) => _values.ContainsKey(key);

    internal string? GetSingleOrDefault(string key) {
        if (!_values.TryGetValue(key, out List<string>? values)) {
            return null;
        }
        if (values.Count != 1) {
            throw new OperatorSyntaxException();
        }
        return values[0];
    }

    internal IReadOnlyList<string> GetRepeated(string key)
        => _values.TryGetValue(key, out List<string>? values)
            ? Array.AsReadOnly(values.ToArray())
            : Array.Empty<string>();
}

internal sealed class OperatorSyntaxException : Exception;

internal sealed class OperatorInputException(Exception innerException)
    : Exception("MemoPod operator input could not be read.", innerException);
