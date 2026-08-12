namespace Atelia.SessionJournal.RecapGrid.Cadence;

internal sealed record CadencePersistenceTestHooks(
    Action<string>? BeforePublish = null,
    Action<string>? AfterPublish = null
) {
    internal static CadencePersistenceTestHooks None { get; } = new();
}
