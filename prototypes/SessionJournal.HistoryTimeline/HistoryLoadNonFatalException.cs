namespace Atelia.SessionJournal.HistoryTimeline;

internal static class HistoryLoadNonFatalException {
    internal static bool IsCatchable(Exception exception)
        => exception is not (
            OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
        );
}
