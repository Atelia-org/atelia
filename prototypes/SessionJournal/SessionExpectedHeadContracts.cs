using Atelia.EventJournal;

namespace Atelia.SessionJournal;

/// <summary>
/// Indicates that entry inspection observed an online operation's captured raw head was no longer
/// current. Later races remain protected by the existing lifecycle and append CAS fences and may
/// retain their existing operational exception type. The caller must restart phase inspection and
/// runtime/config composition from the new head.
/// </summary>
public sealed class SessionJournalExpectedHeadMismatchException
    : InvalidOperationException {
    public SessionJournalExpectedHeadMismatchException(
        EventAddress expectedHead,
        EventAddress? observedHead
    ) : base(
        "SessionJournal branch head changed before the bound online "
        + $"operation. Expected '{expectedHead}', observed "
        + $"'{observedHead}'."
    ) {
        ExpectedHead = expectedHead;
        ObservedHead = observedHead;
    }

    public EventAddress ExpectedHead { get; }

    public EventAddress? ObservedHead { get; }
}
