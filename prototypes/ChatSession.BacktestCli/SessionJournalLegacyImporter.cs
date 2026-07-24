using System.Text;
using System.Text.Json;
using Atelia.ChatSession;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace ChatSessionBacktestCli;

internal sealed record SessionJournalLegacyImportResult(
    int SessionCreatedCount,
    int ConfigurationChangedCount,
    int ObservationCount,
    int AgentActionCount,
    int SkippedCompactionCount,
    int SkippedRecapCount,
    SessionConfiguration FinalConfiguration,
    IReadOnlyList<SessionJournalLegacyImportMapping> Mappings
);

internal sealed record SessionJournalLegacyImportMapping(
    int LegacyOrdinal,
    string LegacyKind,
    string SessionEventKind,
    EventAddress EventAddress
);

internal static class SessionJournalLegacyImporter {
    private const string LegacyMessageKindObservation = "observation";
    private const string LegacyMessageKindAction = "action";
    private const string LegacyMessageKindRecap = "recap";

    public static SessionJournalLegacyImportResult Import(
        ChatSessionLegacyEventSource eventSource,
        string outputPath,
        bool force
    ) {
        ArgumentNullException.ThrowIfNull(eventSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        PrepareOutputPath(outputPath, force);

        SessionJournalEngine? engine = null;
        var mappings = new List<SessionJournalLegacyImportMapping>();
        SessionConfiguration? currentConfiguration = null;
        int sessionCreatedCount = 0;
        int configurationChangedCount = 0;
        int observationCount = 0;
        int agentActionCount = 0;
        int skippedCompactionCount = 0;
        int skippedRecapCount = 0;
        string apiSpecId = "legacy-upgrade-export";

        try {
            foreach (ChatSessionLegacyReplayEvent replayEvent in eventSource.Events) {
                switch (replayEvent.Kind) {
                    case ChatSessionLegacyEventKinds.InitialState: {
                        if (engine is not null) { throw new InvalidDataException("legacy export contains more than one initial-state event."); }

                        currentConfiguration = ToInitialConfiguration(replayEvent);
                        apiSpecId = string.IsNullOrWhiteSpace(replayEvent.Root?.ApiSpecId)
                            ? apiSpecId
                            : replayEvent.Root.ApiSpecId;
                        engine = SessionJournalEngine.Create(outputPath, new SessionCreateOptions(
                            currentConfiguration.ModelId,
                            currentConfiguration.SystemPrompt,
                            currentConfiguration.CompletionSurfaceId,
                            currentConfiguration.Schema
                        ));
                        sessionCreatedCount++;
                        mappings.Add(new SessionJournalLegacyImportMapping(
                            replayEvent.Ordinal,
                            replayEvent.Kind,
                            SessionEventKind.SessionCreated.ToString(),
                            engine.Project().Head ?? throw new InvalidDataException("created SessionJournal has no head.")
                        ));
                        break;
                    }
                    case ChatSessionLegacyEventKinds.ModelTurn: {
                        engine = RequireEngine(engine, replayEvent);
                        foreach (ChatSessionLegacyMessageDto message in RequireMessages(replayEvent.AppendedMessages, replayEvent.Kind, replayEvent.Ordinal)) {
                            switch (message.Kind) {
                                case LegacyMessageKindObservation: {
                                    EventAddress address = engine.AppendObservation(message.Content ?? string.Empty);
                                    observationCount++;
                                    mappings.Add(new SessionJournalLegacyImportMapping(
                                        replayEvent.Ordinal,
                                        replayEvent.Kind,
                                        SessionEventKind.ObservationAccepted.ToString(),
                                        address
                                    ));
                                    break;
                                }
                                case LegacyMessageKindAction: {
                                    EventAddress address = engine.AppendAgentAction(
                                        ToActionMessage(message),
                                        ToCompletionDescriptor(
                                            currentConfiguration ?? throw new InvalidDataException("model-turn appeared before initial configuration."),
                                            apiSpecId
                                        )
                                    );
                                    agentActionCount++;
                                    mappings.Add(new SessionJournalLegacyImportMapping(
                                        replayEvent.Ordinal,
                                        replayEvent.Kind,
                                        SessionEventKind.AgentActionProduced.ToString(),
                                        address
                                    ));
                                    break;
                                }
                                case LegacyMessageKindRecap:
                                    skippedRecapCount++;
                                    break;
                                default:
                                    throw new InvalidDataException($"Unsupported legacy model-turn message kind '{message.Kind}' at ordinal {replayEvent.Ordinal}.");
                            }
                        }
                        break;
                    }
                    case ChatSessionLegacyEventKinds.UpdateSystemPrompt: {
                        engine = RequireEngine(engine, replayEvent);
                        currentConfiguration = ApplySystemPromptChange(currentConfiguration, replayEvent);
                        EventAddress address = engine.AppendSessionConfigurationChanged(currentConfiguration);
                        configurationChangedCount++;
                        mappings.Add(new SessionJournalLegacyImportMapping(
                            replayEvent.Ordinal,
                            replayEvent.Kind,
                            SessionEventKind.SessionConfigurationChanged.ToString(),
                            address
                        ));
                        break;
                    }
                    case ChatSessionLegacyEventKinds.Compaction:
                        skippedCompactionCount++;
                        if (replayEvent.RecapMessage is not null) { skippedRecapCount++; }
                        break;
                    case ChatSessionLegacyEventKinds.RedundantSave:
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported legacy event kind '{replayEvent.Kind}' at ordinal {replayEvent.Ordinal}.");
                }
            }
        }
        catch {
            engine?.Dispose();
            throw;
        }

        if (engine is null || currentConfiguration is null) { throw new InvalidDataException("legacy export did not contain an initial-state event."); }
        engine.Dispose();

        return new SessionJournalLegacyImportResult(
            sessionCreatedCount,
            configurationChangedCount,
            observationCount,
            agentActionCount,
            skippedCompactionCount,
            skippedRecapCount,
            currentConfiguration,
            mappings.AsReadOnly()
        );
    }

    public static void VerifyImportedRepo(string outputPath, SessionJournalLegacyImportResult expected) {
        using var reopened = SessionJournalEngine.Open(outputPath);
        SessionProjection projection = reopened.Project();
        int observations = projection.Context.OfType<ObservationMessage>().Count(message => message is not RecapMessage);
        int actions = projection.Context.OfType<ActionMessage>().Count();

        if (observations != expected.ObservationCount) {
            throw new InvalidDataException($"import smoke failed: projected observation count {observations} != imported {expected.ObservationCount}.");
        }

        if (actions != expected.AgentActionCount) {
            throw new InvalidDataException($"import smoke failed: projected action count {actions} != imported {expected.AgentActionCount}.");
        }

        if (projection.Config is null) { throw new InvalidDataException("import smoke failed: projection is missing final config."); }
        if (!Equals(projection.Config, expected.FinalConfiguration)) {
            throw new InvalidDataException("import smoke failed: projected final config does not match imported final config.");
        }
    }

    public static void WriteReport(
        string reportPath,
        string inputPath,
        string outputPath,
        SessionJournalLegacyImportResult result
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)) ?? ".");
        using var writer = new StreamWriter(reportPath, append: false, Encoding.UTF8);
        writer.WriteLine("# SessionJournal Legacy Import Report");
        writer.WriteLine();
        writer.WriteLine($"- input: `{inputPath}`");
        writer.WriteLine($"- output: `{outputPath}`");
        writer.WriteLine($"- sessionCreated: `{result.SessionCreatedCount}`");
        writer.WriteLine($"- configurationChanged: `{result.ConfigurationChangedCount}`");
        writer.WriteLine($"- observations: `{result.ObservationCount}`");
        writer.WriteLine($"- agentActions: `{result.AgentActionCount}`");
        writer.WriteLine($"- skippedCompactions: `{result.SkippedCompactionCount}`");
        writer.WriteLine($"- skippedRecaps: `{result.SkippedRecapCount}`");
        writer.WriteLine($"- finalModelId: `{result.FinalConfiguration.ModelId}`");
        writer.WriteLine($"- finalCompletionSurfaceId: `{result.FinalConfiguration.CompletionSurfaceId}`");
        writer.WriteLine();
        writer.WriteLine("## Mapping");
        writer.WriteLine();
        writer.WriteLine("| legacy ordinal | legacy kind | session event kind | event address |");
        writer.WriteLine("| ---: | --- | --- | --- |");
        foreach (SessionJournalLegacyImportMapping mapping in result.Mappings) {
            writer.WriteLine($"| {mapping.LegacyOrdinal} | `{mapping.LegacyKind}` | `{mapping.SessionEventKind}` | `{mapping.EventAddress}` |");
        }
    }

    private static void PrepareOutputPath(string outputPath, bool force) {
        string fullPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullPath)) { throw new IOException($"Output path is a file: {fullPath}"); }
        if (!Directory.Exists(fullPath)) { return; }

        bool isEmpty = !Directory.EnumerateFileSystemEntries(fullPath).Any();
        if (!force && !isEmpty) {
            throw new IOException($"Output path already exists and is not empty: {fullPath}. Use --force to replace this SessionJournal repo path.");
        }

        Directory.Delete(fullPath, recursive: true);
    }

    private static SessionConfiguration ToInitialConfiguration(ChatSessionLegacyReplayEvent replayEvent) {
        ChatSessionLegacyRootMetadataDto root = replayEvent.Root
            ?? throw new InvalidDataException("initial-state event is missing root metadata.");

        return new SessionConfiguration(
            RequireNonWhiteSpace(root.ModelId, "initial-state.root.modelId"),
            root.SystemPrompt ?? string.Empty,
            RequireNonWhiteSpace(root.CompletionSurfaceId, "initial-state.root.completionSurfaceId"),
            SessionJournalDefaults.Schema
        );
    }

    private static SessionConfiguration ApplySystemPromptChange(
        SessionConfiguration? currentConfiguration,
        ChatSessionLegacyReplayEvent replayEvent
    ) {
        if (currentConfiguration is null) { throw new InvalidDataException($"update-system-prompt at ordinal {replayEvent.Ordinal} appeared before initial-state."); }

        ChatSessionLegacySystemPromptChangeDto change = replayEvent.SystemPromptChange
            ?? throw new InvalidDataException($"update-system-prompt at ordinal {replayEvent.Ordinal} is missing systemPromptChange.");

        return currentConfiguration with {
            SystemPrompt = change.NewSystemPrompt ?? string.Empty
        };
    }

    private static ActionMessage ToActionMessage(ChatSessionLegacyMessageDto message) {
        if (message.Action is null) { return new ActionMessage(Array.Empty<ActionBlock>()); }

        string json = JsonSerializer.Serialize(
            message.Action.Blocks ?? Array.Empty<SerializedActionBlock>(),
            ChatSessionLegacyEventSourceReader.JsonOptions
        );
        return new ActionMessage(ActionMessageSerialization.DeserializeBlocks(json, options: ChatSessionLegacyEventSourceReader.JsonOptions));
    }

    private static CompletionDescriptor ToCompletionDescriptor(SessionConfiguration configuration, string apiSpecId)
        => new(configuration.CompletionSurfaceId, apiSpecId, configuration.ModelId);

    private static SessionJournalEngine RequireEngine(SessionJournalEngine? engine, ChatSessionLegacyReplayEvent replayEvent)
        => engine ?? throw new InvalidDataException($"{replayEvent.Kind} at ordinal {replayEvent.Ordinal} appeared before initial-state.");

    private static IReadOnlyList<ChatSessionLegacyMessageDto> RequireMessages(
        IReadOnlyList<ChatSessionLegacyMessageDto>? messages,
        string eventKind,
        int ordinal
    )
        => messages ?? throw new InvalidDataException($"{eventKind} event at ordinal {ordinal} is missing appendedMessages.");

    private static string RequireNonWhiteSpace(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"legacy export is missing required value '{name}'.")
            : value;
}
