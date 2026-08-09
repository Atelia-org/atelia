using Atelia.EventJournal;

namespace Atelia.SessionJournal.DerivedRecap.Store;

internal sealed record RecapStoreTestHooks(
    Action? AfterRebuildPageInstalledBeforeCheckpoint = null,
    Action? BeforeRebuildCheckpointReplace = null,
    Action? BeforeRebuildSealInstall = null,
    Action? AfterRebuildDeleteQuarantineRename = null,
    Action<RecapIoPoint, string>? IoObserver = null
);

public sealed record RecapBlockId {
    public const int MaxLength = 128;

    public RecapBlockId(string value) {
        if (!IsValid(value)) {
            throw new ArgumentException(
                "RecapBlockId must match [a-z0-9][a-z0-9._-]{0,127}.",
                nameof(value)
            );
        }
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;

    internal static bool IsValid(string? value) {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength
            || !IsLowerAlphaNumeric(value[0])) {
            return false;
        }
        for (int index = 1; index < value.Length; index++) {
            char ch = value[index];
            if (!IsLowerAlphaNumeric(ch)
                && ch is not ('.' or '_' or '-')) {
                return false;
            }
        }
        return true;
    }

    private static bool IsLowerAlphaNumeric(char ch)
        => (ch >= 'a' && ch <= 'z')
            || (ch >= '0' && ch <= '9');
}

public sealed record DerivedRecapMaterialization(
    EventAddress SetAdmissionAnchor,
    IReadOnlyList<SessionContextContribution> Contributions
);
