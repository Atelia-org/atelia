using System.Security.Cryptography;
using System.Text;
using Atelia.Completion.Abstractions;
using Atelia.EventJournal;
using Atelia.SessionJournal;
using Atelia.SessionJournal.Offline;

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
    string SystemPromptUtf8Sha256CodecId,
    string FinalSystemPromptUtf8Sha256,
    string HistorySemanticCommitmentCodecId,
    string ExpectedHistorySemanticCommitmentSha256,
    EventAddress FinalHead,
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
        var historyContributionHashes = new List<string>();

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
                            engine.InspectExecutionBoundary().Head
                                ?? throw new InvalidDataException(
                                    "created SessionJournal has no head."
                                )
                        ));
                        foreach (
                            LegacyChatSessionMessage message in
                            replayEvent.Messages
                                ?? Array.Empty<LegacyChatSessionMessage>()
                        ) {
                            switch (message.Kind) {
                                case LegacyMessageKindObservation: {
                                    string observationContent =
                                        message.Content ?? string.Empty;
                                    var observation = new ObservationMessage(
                                        observationContent
                                    );
                                    historyContributionHashes.Add(
                                        SessionHistorySemanticCommitment
                                            .ComputeObservationContributionSha256(
                                                observation
                                            )
                                    );
                                    EventAddress address =
                                        engine.AppendObservation(
                                            observationContent
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
                                    ActionMessage action =
                                        ToActionMessage(message);
                                    historyContributionHashes.Add(
                                        SessionHistorySemanticCommitment
                                            .ComputeActionContributionSha256(
                                                action
                                            )
                                    );
                                    EventAddress address =
                                        engine.AppendImportedAgentAction(
                                            action,
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
                                    string observationContent =
                                        message.Content ?? string.Empty;
                                    var observation =
                                        new ObservationMessage(
                                            observationContent
                                        );
                                    historyContributionHashes.Add(
                                        SessionHistorySemanticCommitment
                                            .ComputeObservationContributionSha256(
                                                observation
                                            )
                                    );
                                    EventAddress address =
                                        engine.AppendObservation(
                                            observationContent
                                        );
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
                                    ActionMessage action =
                                        ToActionMessage(message);
                                    historyContributionHashes.Add(
                                        SessionHistorySemanticCommitment
                                            .ComputeActionContributionSha256(
                                                action
                                            )
                                    );
                                    EventAddress address = engine.AppendImportedAgentAction(
                                        action,
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
        EventAddress finalHead =
            engine.InspectExecutionBoundary().Head
            ?? throw new InvalidDataException(
                "imported SessionJournal has no final head."
            );
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
            SessionJournalOfflineValidator
                .SystemPromptUtf8Sha256CodecId,
            ComputeUtf8Sha256(currentSystemPrompt),
            SessionHistorySemanticCommitment.CodecId,
            SessionHistorySemanticCommitment.ComputeSequenceSha256(
                historyContributionHashes
            ),
            finalHead,
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

    public static void VerifyImportedRepo(
        string outputPath,
        SessionJournalLegacyImportResult expected
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(expected);

        SessionJournalOfflineValidationReport report =
            SessionJournalOfflineValidator.ValidateAsync(
                outputPath,
                SessionJournalDefaults.MainBranchName
            ).GetAwaiter().GetResult();

        if (!string.Equals(
                report.Schema,
                SessionJournalOfflineValidator.ReportSchema,
                StringComparison.Ordinal
            )) {
            throw ImportVerificationError(
                $"offline report schema '{report.Schema}' is unsupported"
            );
        }
        if (!string.Equals(
                report.BranchName,
                SessionJournalDefaults.MainBranchName,
                StringComparison.Ordinal
            )) {
            throw ImportVerificationError(
                $"validated branch '{report.BranchName}' is not main"
            );
        }
        string expectedHeadText =
            EventAddressTextCodec.Format(expected.FinalHead);
        if (!string.Equals(
                report.Head,
                expectedHeadText,
                StringComparison.Ordinal
            )) {
            throw ImportVerificationError(
                $"validated head {report.Head ?? "(none)"} does not "
                + $"match source-derived final head {expectedHeadText}"
            );
        }
        if (report.ExecutionPhase != SessionExecutionPhase.Idle) {
            throw ImportVerificationError(
                $"final phase is {report.ExecutionPhase}, expected Idle"
            );
        }
        if (report.HeadKind is not (
                SessionEventKind.SessionCreated
                or SessionEventKind.SystemPromptSetup
                or SessionEventKind.ImportedAgentAction
            )) {
            throw ImportVerificationError(
                $"final head kind {report.HeadKind?.ToString() ?? "(none)"} "
                + "is not a legal settled legacy-import boundary"
            );
        }
        if (report.ToolExecutionSequenceCheckpoint != 0) {
            throw ImportVerificationError(
                "final tool execution sequence checkpoint is not zero"
            );
        }

        RequireImportCount(
            "observation",
            report.ObservationCount,
            expected.ObservationCount
        );
        RequireImportCount(
            "agent action",
            report.AgentActionCount,
            expected.AgentActionCount
        );
        RequireImportCount(
            "imported agent action",
            report.ImportedAgentActionCount,
            expected.AgentActionCount
        );
        RequireImportCount(
            "history contribution",
            report.HistoryContributionCount,
            checked(
                expected.ObservationCount
                + expected.AgentActionCount
            )
        );
        RequireImportCount(
            "Prepared request",
            report.PreparedRequestCount,
            0
        );
        RequireImportCount(
            "tool-result history",
            report.ToolResultHistoryCount,
            0
        );

        var expectedEventKindCounts =
            new Dictionary<SessionEventKind, int> {
                [SessionEventKind.RuntimeConfigSetup] =
                    expected.RuntimeConfigSetupCount,
                [SessionEventKind.SystemPromptSetup] =
                    expected.SystemPromptSetupCount,
                [SessionEventKind.SessionCreated] =
                    expected.SessionCreatedCount,
                [SessionEventKind.ObservationAccepted] =
                    expected.ObservationCount,
                [SessionEventKind.ImportedAgentAction] =
                    expected.AgentActionCount
            };
        expectedEventKindCounts = expectedEventKindCounts
            .Where(static pair => pair.Value != 0)
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value
            );
        Dictionary<SessionEventKind, int> actualEventKindCounts =
            report.EventKindCounts.ToDictionary(
                static entry => entry.Kind,
                static entry => entry.Count
            );
        if (!expectedEventKindCounts.OrderBy(static pair => pair.Key)
            .SequenceEqual(
                actualEventKindCounts.OrderBy(static pair => pair.Key)
            )) {
            throw ImportVerificationError(
                "event-kind counts do not exactly match import counters"
            );
        }
        int expectedEventCount = checked(
            expected.SessionCreatedCount
            + expected.RuntimeConfigSetupCount
            + expected.SystemPromptSetupCount
            + expected.ObservationCount
            + expected.AgentActionCount
        );
        RequireImportCount(
            "event",
            report.EventCount,
            expectedEventCount
        );

        if (report.RuntimeConfig != expected.FinalConfiguration) {
            throw ImportVerificationError(
                "final runtime configuration does not match source"
            );
        }
        if (!string.Equals(
                report.SystemPromptUtf8Sha256CodecId,
                expected.SystemPromptUtf8Sha256CodecId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                report.SystemPromptUtf8Sha256,
                expected.FinalSystemPromptUtf8Sha256,
                StringComparison.Ordinal
            )) {
            throw ImportVerificationError(
                "final system prompt hash does not match source"
            );
        }
        if (!string.Equals(
                report.HistorySemanticCommitmentCodecId,
                expected.HistorySemanticCommitmentCodecId,
                StringComparison.Ordinal
            )
            || !string.Equals(
                report.HistorySemanticCommitmentSha256,
                expected.ExpectedHistorySemanticCommitmentSha256,
                StringComparison.Ordinal
            )) {
            throw ImportVerificationError(
                "semantic history commitment does not match source"
            );
        }

        using SessionJournalEngine readOnly =
            SessionJournalEngine.OpenReadOnly(
                outputPath,
                SessionJournalDefaults.MainBranchName
            );
        if (!string.Equals(
                report.BranchRefId,
                readOnly.BranchRefId.ToHexString(),
                StringComparison.Ordinal
            )
            || !string.Equals(
                report.BranchName,
                readOnly.BranchName,
                StringComparison.Ordinal
            )) {
            throw ImportVerificationError(
                "validated branch identity does not match the current "
                + "main branch ref"
            );
        }
        SessionExecutionBoundaryInspection boundary =
            readOnly.InspectExecutionBoundary();
        if (boundary.Head != expected.FinalHead
            || boundary.Phase != report.ExecutionPhase
            || boundary.HeadKind != report.HeadKind) {
            throw ImportVerificationError(
                "current exact execution boundary does not match "
                + "the validated report and source-derived final head"
            );
        }
        SessionCurrentLineageSnapshot lineage =
            readOnly.ReadCurrentLineageHeaders();
        if (lineage.CapturedHead != expected.FinalHead
            || lineage.HeadToRoot.Count != report.EventCount) {
            throw ImportVerificationError(
                "captured current lineage does not match validated "
                + "head/event count"
            );
        }
        ValidateMappings(expected, lineage);

        SessionGoverningSetup setup =
            readOnly.ResolveGoverningSetup(expected.FinalHead);
        if (setup.RuntimeConfig != expected.FinalConfiguration
            || !string.Equals(
                ComputeUtf8Sha256(setup.SystemPrompt),
                expected.FinalSystemPromptUtf8Sha256,
                StringComparison.Ordinal
            )
            || !string.Equals(
                report.RuntimeConfigSetup,
                EventAddressTextCodec.Format(
                    setup.RuntimeConfigSetupAddress
                ),
                StringComparison.Ordinal
            )
            || !string.Equals(
                report.SystemPromptSetup,
                EventAddressTextCodec.Format(
                    setup.SystemPromptSetupAddress
                ),
                StringComparison.Ordinal
            )) {
            throw ImportVerificationError(
                "exact-head governing setup does not match source/report"
            );
        }
    }

    private static void ValidateMappings(
        SessionJournalLegacyImportResult expected,
        SessionCurrentLineageSnapshot lineage
    ) {
        int expectedMappingCount = checked(
            expected.SessionCreatedCount
            + expected.ObservationCount
            + expected.AgentActionCount
            + expected.SystemPromptSetupCount
            - 1
        );
        RequireImportCount(
            "mapping",
            expected.Mappings.Count,
            expectedMappingCount
        );
        if (expected.Mappings.Count == 0) {
            throw ImportVerificationError(
                "import result has no SessionCreated mapping"
            );
        }
        if (expected.Mappings[^1].EventAddress
            != expected.FinalHead) {
            throw ImportVerificationError(
                "final mapping does not identify the final imported head"
            );
        }

        var lineageByAddress =
            new Dictionary<
                EventAddress,
                (SessionCurrentLineageHeader Header, int Index)
            >();
        for (int index = 0;
             index < lineage.HeadToRoot.Count;
             index++) {
            SessionCurrentLineageHeader header =
                lineage.HeadToRoot[index];
            if (!lineageByAddress.TryAdd(
                    header.Address,
                    (header, index)
                )) {
                throw ImportVerificationError(
                    $"current lineage repeats address {header.Address}"
                );
            }
        }

        var mappedAddresses = new HashSet<EventAddress>();
        var mappedKindCounts =
            new Dictionary<SessionEventKind, int>();
        int previousHeadToRootIndex = lineage.HeadToRoot.Count;
        foreach (
            SessionJournalLegacyImportMapping mapping
            in expected.Mappings
        ) {
            if (!mappedAddresses.Add(mapping.EventAddress)) {
                throw ImportVerificationError(
                    $"mapping repeats address {mapping.EventAddress}"
                );
            }
            if (!lineageByAddress.TryGetValue(
                    mapping.EventAddress,
                    out var located
                )) {
                throw ImportVerificationError(
                    $"mapping address {mapping.EventAddress} is not "
                    + "on the captured current lineage"
                );
            }
            if (!Enum.TryParse(
                    mapping.SessionEventKind,
                    ignoreCase: false,
                    out SessionEventKind mappedKind
                )
                || !string.Equals(
                    mappedKind.ToString(),
                    mapping.SessionEventKind,
                    StringComparison.Ordinal
                )
                || located.Header.Kind != mappedKind) {
                throw ImportVerificationError(
                    $"mapping kind '{mapping.SessionEventKind}' does "
                    + $"not match raw event {mapping.EventAddress}"
                );
            }
            if (located.Index >= previousHeadToRootIndex) {
                throw ImportVerificationError(
                    "mapping addresses are not strictly ordered from "
                    + "root toward final head"
                );
            }
            previousHeadToRootIndex = located.Index;
            mappedKindCounts[mappedKind] = checked(
                mappedKindCounts.GetValueOrDefault(mappedKind) + 1
            );
        }
        if (previousHeadToRootIndex != 0) {
            throw ImportVerificationError(
                "final mapping is not the captured current head"
            );
        }

        var expectedMappedKindCounts =
            new Dictionary<SessionEventKind, int> {
                [SessionEventKind.SessionCreated] =
                    expected.SessionCreatedCount,
                [SessionEventKind.ObservationAccepted] =
                    expected.ObservationCount,
                [SessionEventKind.ImportedAgentAction] =
                    expected.AgentActionCount,
                [SessionEventKind.SystemPromptSetup] =
                    expected.SystemPromptSetupCount - 1
            };
        expectedMappedKindCounts = expectedMappedKindCounts
            .Where(static pair => pair.Value != 0)
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value
            );
        if (!expectedMappedKindCounts.OrderBy(static pair => pair.Key)
            .SequenceEqual(
                mappedKindCounts.OrderBy(static pair => pair.Key)
            )) {
            throw ImportVerificationError(
                "mapping event-kind counts do not match import counters"
            );
        }
    }

    private static void RequireImportCount(
        string name,
        int actual,
        int expected
    ) {
        if (actual != expected) {
            throw ImportVerificationError(
                $"{name} count {actual} does not match imported "
                + expected
            );
        }
    }

    private static InvalidDataException ImportVerificationError(
        string message
    ) => new($"import verification failed: {message}.");

    private static string ComputeUtf8Sha256(string value) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(value))
        );

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
        writer.WriteLine($"- finalHead: `{result.FinalHead}`");
        writer.WriteLine(
            "- systemPromptUtf8Sha256CodecId: "
            + $"`{result.SystemPromptUtf8Sha256CodecId}`"
        );
        writer.WriteLine(
            "- finalSystemPromptUtf8Sha256: "
            + $"`{result.FinalSystemPromptUtf8Sha256}`"
        );
        writer.WriteLine(
            "- historySemanticCommitmentCodecId: "
            + $"`{result.HistorySemanticCommitmentCodecId}`"
        );
        writer.WriteLine(
            "- expectedHistorySemanticCommitmentSha256: "
            + $"`{result.ExpectedHistorySemanticCommitmentSha256}`"
        );
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
