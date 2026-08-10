namespace Atelia.SessionJournal.RecapGrid.Control;

/// <summary>
/// Process-death failpoints owned by persistence tests. Public production
/// entry points always use <see cref="None"/>.
/// </summary>
internal sealed record ControlPersistenceTestHooks(
    Action? BeforeStatePublish = null,
    Action<string>? AfterStatePublish = null,
    Action? BeforeBackupPublish = null,
    Action? AfterBackupPublish = null
) {
    internal static ControlPersistenceTestHooks None { get; } = new();
}
