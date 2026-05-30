using System.Text.Json;
using System.Text.Json.Serialization;
using Atelia.TextAdv2.ReadOnlyView;

namespace Atelia.TextAdv2.Runtime;

internal sealed record TextAdv2RuntimePersistentState(
    int SchemaVersion,
    long CurrentTick,
    Dictionary<string, ActorMovementObservation[]> MovementHistoryByActor
) {
    public const int CurrentSchemaVersion = 1;

    public static TextAdv2RuntimePersistentState Empty { get; } = new(
        CurrentSchemaVersion,
        0,
        new Dictionary<string, ActorMovementObservation[]>(StringComparer.Ordinal)
    );
}

internal sealed class TextAdv2RuntimePersistentStateStore {
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public TextAdv2RuntimePersistentStateStore(string repoDir) {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoDir);
        StateFilePath = Path.Combine(repoDir, ".textadv2-runtime-state.json");
    }

    public string StateFilePath { get; }

    public TextAdv2RuntimePersistentState LoadOrDefault() {
        if (!File.Exists(StateFilePath)) { return TextAdv2RuntimePersistentState.Empty; }

        string json = File.ReadAllText(StateFilePath);
        var state = JsonSerializer.Deserialize<TextAdv2RuntimePersistentState>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Runtime state file '{StateFilePath}' did not contain a valid state document.");

        if (state.SchemaVersion != TextAdv2RuntimePersistentState.CurrentSchemaVersion) {
            throw new InvalidOperationException(
                $"Runtime state file '{StateFilePath}' uses schema version {state.SchemaVersion}, but only {TextAdv2RuntimePersistentState.CurrentSchemaVersion} is supported."
            );
        }

        return state;
    }

    public void Save(TextAdv2RuntimePersistentState state) {
        ArgumentNullException.ThrowIfNull(state);

        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        string json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(StateFilePath, json);
    }

    private static JsonSerializerOptions CreateJsonOptions() {
        var options = new JsonSerializerOptions {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
