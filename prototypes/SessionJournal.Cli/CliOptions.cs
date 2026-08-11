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
            string option = arg[2..];
            int equals = option.IndexOf('=', StringComparison.Ordinal);
            string key = equals < 0 ? option : option[..equals];
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
            if (equals >= 0) {
                occurrences.Add(option[(equals + 1)..]);
                continue;
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

    public bool HasSingleFlag(string key) {
        if (!_values.TryGetValue(
                key,
                out List<string?>? occurrences
            )) {
            return false;
        }
        if (occurrences.Count != 1) {
            throw new ArgumentException(
                $"Flag --{key} must be specified at most once."
            );
        }
        string? value = occurrences[0];
        if (value is null) {
            return true;
        }
        return bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new ArgumentException(
                $"--{key} accepts only true or false."
            );
    }

    public void EnsureOnly(params string[] allowedKeys) {
        var allowed = allowedKeys.ToHashSet(StringComparer.Ordinal);
        string? unexpected = _values.Keys
            .OrderBy(static key => key, StringComparer.Ordinal)
            .FirstOrDefault(key => !allowed.Contains(key));
        if (unexpected is not null) {
            throw new ArgumentException(
                $"Unknown option --{unexpected}."
            );
        }
    }

    public string RequireSingle(string key) {
        if (!_values.TryGetValue(key, out List<string?>? values)
            || values.Count == 0
            || string.IsNullOrWhiteSpace(values[0])) {
            throw new ArgumentException(
                $"Missing required option --{key}."
            );
        }
        if (values.Count != 1) {
            throw new ArgumentException(
                $"Option --{key} must be specified exactly once."
            );
        }
        return values[0]!;
    }

    public string? GetOptionalSingle(string key) {
        if (!_values.TryGetValue(key, out List<string?>? values)) {
            return null;
        }
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])) {
            throw new ArgumentException(
                $"Option --{key} accepts exactly one non-empty value."
            );
        }
        return values[0];
    }

    public IReadOnlyList<string> RequireRepeated(string key) {
        IReadOnlyList<string> values = GetRepeated(key);
        return values.Count == 0
            ? throw new ArgumentException(
                $"Missing required option --{key}."
            )
            : values;
    }

    public IReadOnlyList<string> GetRepeated(string key) {
        if (!_values.TryGetValue(key, out List<string?>? values)) {
            return Array.Empty<string>();
        }
        if (values.Any(static value => string.IsNullOrWhiteSpace(value))) {
            throw new ArgumentException(
                $"Option --{key} requires a non-empty value for every occurrence."
            );
        }
        return Array.AsReadOnly([.. values.Select(static value => value!)]);
    }
}
