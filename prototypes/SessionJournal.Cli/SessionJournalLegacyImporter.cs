using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;

namespace Atelia.SessionJournal.Cli;

internal sealed record SessionJournalLegacyImportResult(
    int SessionCreatedCount,
    int RuntimeConfigSetupCount,
    int SystemPromptSetupCount,
    int ObservationCount,
    int AgentActionCount,
    int SkippedCompactionCount,
    int SkippedRecapCount,
    SessionRuntimeConfiguration FinalConfiguration,
    string FinalSystemPrompt,
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
        LegacyChatSessionExport eventSource,
        string outputPath,
        bool force
    ) {
        ArgumentNullException.ThrowIfNull(eventSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        ValidateImportable(eventSource);
        string fullOutputPath = Path.GetFullPath(outputPath);
        EnsureOutputCanBeReplaced(fullOutputPath, force);
        string stagingPath = CreateSiblingRepositoryPath(
            fullOutputPath,
            "importing"
        );
        try {
            SessionJournalLegacyImportResult result = ImportIntoRepository(
                eventSource,
                stagingPath
            );
            VerifyImportedRepo(stagingPath, result);
            PublishStagedRepository(
                stagingPath,
                fullOutputPath,
                force
            );
            return result;
        }
        finally {
            TryDeleteDirectory(stagingPath);
        }
    }

    private static SessionJournalLegacyImportResult ImportIntoRepository(
        LegacyChatSessionExport eventSource,
        string outputPath
    ) {

        SessionJournalEngine? engine = null;
        var mappings = new List<SessionJournalLegacyImportMapping>();
        SessionRuntimeConfiguration? currentConfiguration = null;
        string? currentSystemPrompt = null;
        int sessionCreatedCount = 0;
        int runtimeConfigSetupCount = 0;
        int systemPromptSetupCount = 0;
        int observationCount = 0;
        int agentActionCount = 0;
        int skippedCompactionCount = 0;
        int skippedRecapCount = 0;
        string apiSpecId = "legacy-upgrade-export";

        try {
            foreach (LegacyChatSessionEvent replayEvent in eventSource.Events) {
                switch (replayEvent.Kind) {
                    case LegacyChatSessionEventKinds.InitialState: {
                        if (engine is not null) { throw new InvalidDataException("legacy export contains more than one initial-state event."); }

                        currentConfiguration = ToInitialConfiguration(replayEvent);
                        currentSystemPrompt = replayEvent.Root?.SystemPrompt ?? string.Empty;
                        apiSpecId = string.IsNullOrWhiteSpace(replayEvent.Root?.ApiSpecId)
                            ? apiSpecId
                            : replayEvent.Root.ApiSpecId;
                        engine = SessionJournalEngine.Create(outputPath, new SessionCreateOptions(
                            currentConfiguration.ModelId,
                            currentSystemPrompt,
                            currentConfiguration.CompletionSurfaceId,
                            currentConfiguration.Schema,
                            DerivedContextNthPrevious: 0,
                            Origin: SessionCreationOrigin.LegacyImport
                        ));
                        runtimeConfigSetupCount++;
                        systemPromptSetupCount++;
                        sessionCreatedCount++;
                        mappings.Add(new SessionJournalLegacyImportMapping(
                            replayEvent.Ordinal,
                            replayEvent.Kind,
                            SessionEventKind.SessionCreated.ToString(),
                            engine.Project().Head ?? throw new InvalidDataException("created SessionJournal has no head.")
                        ));
                        foreach (
                            LegacyChatSessionMessage message in
                            replayEvent.Messages
                                ?? Array.Empty<LegacyChatSessionMessage>()
                        ) {
                            switch (message.Kind) {
                                case LegacyMessageKindObservation: {
                                    EventAddress address =
                                        engine.AppendObservation(
                                            message.Content ?? string.Empty
                                        );
                                    observationCount++;
                                    mappings.Add(
                                        new SessionJournalLegacyImportMapping(
                                            replayEvent.Ordinal,
                                            replayEvent.Kind,
                                            SessionEventKind
                                                .ObservationAccepted
                                                .ToString(),
                                            address
                                        )
                                    );
                                    break;
                                }
                                case LegacyMessageKindAction: {
                                    EventAddress address =
                                        engine.AppendImportedAgentAction(
                                            ToActionMessage(message),
                                            ToCompletionDescriptor(
                                                currentConfiguration,
                                                apiSpecId
                                            )
                                        );
                                    agentActionCount++;
                                    mappings.Add(
                                        new SessionJournalLegacyImportMapping(
                                            replayEvent.Ordinal,
                                            replayEvent.Kind,
                                            SessionEventKind
                                                .ImportedAgentAction
                                                .ToString(),
                                            address
                                        )
                                    );
                                    break;
                                }
                                case LegacyMessageKindRecap:
                                    skippedRecapCount++;
                                    break;
                            }
                        }
                        break;
                    }
                    case LegacyChatSessionEventKinds.ModelTurn: {
                        engine = RequireEngine(engine, replayEvent);
                        foreach (LegacyChatSessionMessage message in RequireMessages(replayEvent.AppendedMessages, replayEvent.Kind, replayEvent.Ordinal)) {
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
                                    EventAddress address = engine.AppendImportedAgentAction(
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
                                        SessionEventKind.ImportedAgentAction.ToString(),
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
                    case LegacyChatSessionEventKinds.UpdateSystemPrompt: {
                        engine = RequireEngine(engine, replayEvent);
                        currentSystemPrompt = ReadSystemPromptChange(currentSystemPrompt, replayEvent);
                        EventAddress address = engine.AppendSystemPromptSetup(currentSystemPrompt);
                        systemPromptSetupCount++;
                        mappings.Add(new SessionJournalLegacyImportMapping(
                            replayEvent.Ordinal,
                            replayEvent.Kind,
                            SessionEventKind.SystemPromptSetup.ToString(),
                            address
                        ));
                        break;
                    }
                    case LegacyChatSessionEventKinds.Compaction:
                        skippedCompactionCount++;
                        if (replayEvent.RecapMessage is not null) { skippedRecapCount++; }
                        break;
                    case LegacyChatSessionEventKinds.RedundantSave:
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

        if (engine is null || currentConfiguration is null || currentSystemPrompt is null) { throw new InvalidDataException("legacy export did not contain an initial-state event."); }
        engine.Dispose();

        return new SessionJournalLegacyImportResult(
            sessionCreatedCount,
            runtimeConfigSetupCount,
            systemPromptSetupCount,
            observationCount,
            agentActionCount,
            skippedCompactionCount,
            skippedRecapCount,
            currentConfiguration,
            currentSystemPrompt,
            mappings.AsReadOnly()
        );
    }

    private static void ValidateImportable(LegacyChatSessionExport export) {
        bool hasInitialState = false;
        bool awaitingAgentAction = false;
        for (int index = 0; index < export.Events.Count; index++) {
            LegacyChatSessionEvent replayEvent = export.Events[index];
            if (replayEvent.Ordinal != index) {
                throw new InvalidDataException(
                    $"Legacy event ordinal mismatch at index {index}: "
                    + $"{replayEvent.Ordinal}."
                );
            }

            switch (replayEvent.Kind) {
                case LegacyChatSessionEventKinds.InitialState:
                    if (hasInitialState || index != 0) {
                        throw new InvalidDataException(
                            "Legacy export must contain exactly one "
                            + "initial-state event at ordinal 0."
                        );
                    }
                    _ = ToInitialConfiguration(replayEvent);
                    ValidateMessages(
                        replayEvent.Messages
                            ?? Array.Empty<LegacyChatSessionMessage>(),
                        replayEvent.Kind,
                        replayEvent.Ordinal,
                        ref awaitingAgentAction
                    );
                    hasInitialState = true;
                    break;
                case LegacyChatSessionEventKinds.ModelTurn:
                    RequireInitialState(hasInitialState, replayEvent);
                    ValidateMessages(
                        RequireMessages(
                            replayEvent.AppendedMessages,
                            replayEvent.Kind,
                            replayEvent.Ordinal
                        ),
                        replayEvent.Kind,
                        replayEvent.Ordinal,
                        ref awaitingAgentAction
                    );
                    break;
                case LegacyChatSessionEventKinds.UpdateSystemPrompt:
                    RequireInitialState(hasInitialState, replayEvent);
                    if (awaitingAgentAction) {
                        throw new InvalidDataException(
                            $"update-system-prompt at ordinal "
                            + $"{replayEvent.Ordinal} appeared while an "
                            + "observation was awaiting an agent action."
                        );
                    }
                    if (replayEvent.SystemPromptChange is null) {
                        throw new InvalidDataException(
                            $"update-system-prompt at ordinal "
                            + $"{replayEvent.Ordinal} is missing "
                            + "systemPromptChange."
                        );
                    }
                    break;
                case LegacyChatSessionEventKinds.Compaction:
                case LegacyChatSessionEventKinds.RedundantSave:
                    RequireInitialState(hasInitialState, replayEvent);
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported legacy event kind "
                        + $"'{replayEvent.Kind}' at ordinal "
                        + $"{replayEvent.Ordinal}."
                    );
            }
        }

        if (!hasInitialState) {
            throw new InvalidDataException(
                "Legacy export did not contain an initial-state event."
            );
        }
    }

    private static void ValidateMessages(
        IReadOnlyList<LegacyChatSessionMessage> messages,
        string eventKind,
        int ordinal,
        ref bool awaitingAgentAction
    ) {
        foreach (LegacyChatSessionMessage message in messages) {
            switch (message.Kind) {
                case LegacyMessageKindObservation:
                    if (awaitingAgentAction) {
                        throw new InvalidDataException(
                            $"Legacy {eventKind} observation at ordinal "
                            + $"{ordinal} appeared before the previous "
                            + "observation received an agent action."
                        );
                    }
                    awaitingAgentAction = true;
                    break;
                case LegacyMessageKindAction:
                    if (!awaitingAgentAction) {
                        throw new InvalidDataException(
                            $"Legacy {eventKind} action at ordinal "
                            + $"{ordinal} has no preceding observation."
                        );
                    }
                    if (
                        message.Action?.Blocks.Any(
                            static block =>
                                string.Equals(
                                    block.Kind,
                                    ActionMessageSerialization
                                        .BlockKindToolCall,
                                    StringComparison.Ordinal
                                )
                        ) == true
                    ) {
                        throw new NotSupportedException(
                            $"Legacy {eventKind} action at ordinal "
                            + $"{ordinal} contains tool calls. Importing "
                            + "legacy tool execution requires a dedicated "
                            + "SessionJournal raw migration contract."
                        );
                    }
                    _ = ToActionMessage(message);
                    awaitingAgentAction = false;
                    break;
                case LegacyMessageKindRecap:
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported legacy {eventKind} message kind "
                        + $"'{message.Kind}' at ordinal {ordinal}."
                    );
            }
        }
    }

    private static void RequireInitialState(
        bool hasInitialState,
        LegacyChatSessionEvent replayEvent
    ) {
        if (!hasInitialState) {
            throw new InvalidDataException(
                $"{replayEvent.Kind} at ordinal {replayEvent.Ordinal} "
                + "appeared before initial-state."
            );
        }
    }

    public static void VerifyImportedRepo(string outputPath, SessionJournalLegacyImportResult expected) {
        using var reopened = SessionJournalEngine.Open(outputPath);
        SessionProjection projection = reopened.Project();
        int observations = projection.Context
            .OfType<ObservationMessage>()
            .Count();
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

        if (!string.Equals(projection.SystemPrompt, expected.FinalSystemPrompt, StringComparison.Ordinal)) {
            throw new InvalidDataException("import smoke failed: projected final system prompt does not match imported final system prompt.");
        }
    }

    public static void WriteReport(
        string reportPath,
        string inputPath,
        string outputPath,
        SessionJournalLegacyImportResult result
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        string fullReportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullReportPath) ?? "."
        );
        var content = new StringBuilder();
        using var writer = new StringWriter(content);
        writer.WriteLine("# SessionJournal Legacy Import Report");
        writer.WriteLine();
        writer.WriteLine($"- input: `{inputPath}`");
        writer.WriteLine($"- output: `{outputPath}`");
        writer.WriteLine($"- sessionCreated: `{result.SessionCreatedCount}`");
        writer.WriteLine($"- runtimeConfigSetups: `{result.RuntimeConfigSetupCount}`");
        writer.WriteLine($"- systemPromptSetups: `{result.SystemPromptSetupCount}`");
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
        WriteTextAtomically(fullReportPath, content.ToString());
    }

    private static void EnsureOutputCanBeReplaced(
        string fullPath,
        bool force
    ) {
        if (File.Exists(fullPath)) { throw new IOException($"Output path is a file: {fullPath}"); }
        if (!Directory.Exists(fullPath)) { return; }

        bool isEmpty = !Directory.EnumerateFileSystemEntries(fullPath).Any();
        if (!force && !isEmpty) {
            throw new IOException($"Output path already exists and is not empty: {fullPath}. Use --force to replace this SessionJournal repo path.");
        }
    }

    private static string CreateSiblingRepositoryPath(
        string fullOutputPath,
        string purpose
    ) {
        string parentPath =
            Path.GetDirectoryName(fullOutputPath)
            ?? throw new IOException(
                $"Output path has no parent directory: {fullOutputPath}"
            );
        Directory.CreateDirectory(parentPath);
        string leafName = Path.GetFileName(fullOutputPath);
        while (true) {
            string candidate = Path.Combine(
                parentPath,
                $".{leafName}.{purpose}.{Guid.NewGuid():N}"
            );
            if (!File.Exists(candidate)
                && !Directory.Exists(candidate)) {
                return candidate;
            }
        }
    }

    private static void PublishStagedRepository(
        string stagingPath,
        string fullOutputPath,
        bool force
    ) {
        EnsureOutputCanBeReplaced(fullOutputPath, force);
        string? backupPath = null;
        if (Directory.Exists(fullOutputPath)) {
            backupPath = CreateSiblingRepositoryPath(
                fullOutputPath,
                "replaced"
            );
            Directory.Move(fullOutputPath, backupPath);
        }

        try {
            Directory.Move(stagingPath, fullOutputPath);
        }
        catch (Exception publishException) {
            if (backupPath is not null
                && Directory.Exists(backupPath)
                && !Directory.Exists(fullOutputPath)) {
                try {
                    Directory.Move(backupPath, fullOutputPath);
                }
                catch (Exception restoreException) {
                    throw new IOException(
                        "Failed to publish the imported SessionJournal "
                        + "repository and failed to restore the previous "
                        + $"repository at '{fullOutputPath}'.",
                        new AggregateException(
                            publishException,
                            restoreException
                        )
                    );
                }
            }
            throw;
        }

        if (backupPath is not null) {
            TryDeleteDirectory(backupPath);
        }
    }

    private static void WriteTextAtomically(
        string fullOutputPath,
        string content
    ) {
        string directory =
            Path.GetDirectoryName(fullOutputPath) ?? ".";
        string fileName = Path.GetFileName(fullOutputPath);
        string temporaryPath = Path.Combine(
            directory,
            $".{fileName}.{Guid.NewGuid():N}.tmp"
        );
        try {
            using (
                var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read
                )
            )
            using (
                var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false
                    )
                )
            ) {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        catch {
            try {
                File.Delete(temporaryPath);
            }
            catch {
                // Best-effort cleanup must not hide the original failure.
            }
            throw;
        }
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path)) {
                Directory.Delete(path, recursive: true);
            }
        }
        catch {
            // Best-effort cleanup must not hide the original failure.
        }
    }

    private static SessionRuntimeConfiguration ToInitialConfiguration(LegacyChatSessionEvent replayEvent) {
        LegacyChatSessionRoot root = replayEvent.Root
            ?? throw new InvalidDataException("initial-state event is missing root metadata.");

        return new SessionRuntimeConfiguration(
            RequireNonWhiteSpace(root.ModelId, "initial-state.root.modelId"),
            RequireNonWhiteSpace(root.CompletionSurfaceId, "initial-state.root.completionSurfaceId"),
            SessionJournalDefaults.Schema,
            new SessionDerivedContextConfiguration(0)
        );
    }

    private static string ReadSystemPromptChange(
        string? currentSystemPrompt,
        LegacyChatSessionEvent replayEvent
    ) {
        if (currentSystemPrompt is null) { throw new InvalidDataException($"update-system-prompt at ordinal {replayEvent.Ordinal} appeared before initial-state."); }

        LegacyChatSessionSystemPromptChange change = replayEvent.SystemPromptChange
            ?? throw new InvalidDataException($"update-system-prompt at ordinal {replayEvent.Ordinal} is missing systemPromptChange.");

        return change.NewSystemPrompt ?? string.Empty;
    }

    private static ActionMessage ToActionMessage(LegacyChatSessionMessage message) {
        if (message.Action is null) { return new ActionMessage(Array.Empty<ActionBlock>()); }

        return new ActionMessage(
            ActionMessageSerialization.FromSerializedBlocks(
                message.Action.Blocks ?? Array.Empty<SerializedActionBlock>()
            )
        );
    }

    private static CompletionDescriptor ToCompletionDescriptor(SessionRuntimeConfiguration configuration, string apiSpecId)
        => new(configuration.CompletionSurfaceId, apiSpecId, configuration.ModelId);

    private static SessionJournalEngine RequireEngine(SessionJournalEngine? engine, LegacyChatSessionEvent replayEvent)
        => engine ?? throw new InvalidDataException($"{replayEvent.Kind} at ordinal {replayEvent.Ordinal} appeared before initial-state.");

    private static IReadOnlyList<LegacyChatSessionMessage> RequireMessages(
        IReadOnlyList<LegacyChatSessionMessage>? messages,
        string eventKind,
        int ordinal
    )
        => messages ?? throw new InvalidDataException($"{eventKind} event at ordinal {ordinal} is missing appendedMessages.");

    private static string RequireNonWhiteSpace(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException($"legacy export is missing required value '{name}'.")
            : value;
}
