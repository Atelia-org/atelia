namespace Atelia.SessionJournal.Cli;

internal sealed class CliOptions {
    private readonly Dictionary<string, List<string?>> _values;

    private CliOptions(Dictionary<string, List<string?>> values) {
        _values = values;
    }

    public static CliOptions Parse(string[] args) {
        var values =
            new Dictionary<string, List<string?>>(StringComparer.Ordinal);
        for (int index = 0; index < args.Length; index++) {
            string arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) {
                throw new ArgumentException(
                    $"Unexpected argument '{arg}'."
                );
            }
            string key = arg[2..];
            if (string.IsNullOrWhiteSpace(key)) {
                throw new ArgumentException("Empty option name.");
            }
            if (!values.TryGetValue(
                    key,
                    out List<string?>? occurrences
                )) {
                occurrences = [];
                values.Add(key, occurrences);
            }
            if (index + 1 >= args.Length
                || args[index + 1].StartsWith(
                    "--",
                    StringComparison.Ordinal
                )) {
                occurrences.Add(null);
                continue;
            }
            occurrences.Add(args[++index]);
        }
        return new CliOptions(values);
    }

    public string Require(string key) {
        string? value = Get(key);
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                $"Missing required option --{key}."
            )
            : value;
    }

    public string? Get(string key)
        => _values.TryGetValue(key, out List<string?>? values)
            ? values[^1]
            : null;

    public IReadOnlyList<string?> GetAll(string key)
        => _values.TryGetValue(key, out List<string?>? values)
            ? values.AsReadOnly()
            : Array.AsReadOnly(Array.Empty<string?>());

    public int GetInt(string key, int defaultValue) {
        string? value = Get(key);
        if (string.IsNullOrWhiteSpace(value)) { return defaultValue; }
        return int.TryParse(value, out int parsed) && parsed > 0
            ? parsed
            : throw new ArgumentException(
                $"--{key} must be a positive integer."
            );
    }

    public bool HasFlag(string key) {
        if (!_values.TryGetValue(
                key,
                out List<string?>? occurrences
            )) {
            return false;
        }

        string? value = occurrences[^1];
        if (value is null) { return true; }
        return bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new ArgumentException(
                $"--{key} accepts only true or false."
            );
    }
}
