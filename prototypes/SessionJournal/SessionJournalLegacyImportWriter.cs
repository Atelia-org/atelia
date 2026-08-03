using Atelia.Completion.Abstractions;
using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Create-only authority for importing legacy conversation history. Every
/// repository created through this surface is durably marked as
/// <see cref="SessionCreationOrigin.LegacyImport"/>.
/// </summary>
public sealed class SessionJournalLegacyImportWriter : IDisposable {
    private readonly SessionJournalEngine _engine;

    private SessionJournalLegacyImportWriter(
        SessionJournalEngine engine
    ) {
        _engine = engine;
    }

    public static SessionJournalLegacyImportWriter Create(
        string path,
        SessionCreateOptions options
    ) => new(SessionJournalEngine.CreateCore(
        path,
        options,
        SessionCreationOrigin.LegacyImport,
        runtime: null,
        testHooks: null
    ));

    public EventAddress ReadCurrentHead()
        => _engine.ReadCurrentHead()
            ?? throw new InvalidDataException(
                "Created legacy import repository has no current head."
            );

    public EventAddress AppendObservation(string content)
        => _engine.AppendObservation(content);

    public EventAddress AppendSystemPromptSetup(string systemPrompt)
        => _engine.AppendSystemPromptSetup(systemPrompt);

    public EventAddress AppendImportedAgentAction(
        ActionMessage action,
        CompletionDescriptor invocation
    ) => _engine.AppendImportedAgentAction(action, invocation);

    public void Dispose() => _engine.Dispose();
}
