namespace Atelia.SessionJournal.DerivedRecap.Planner;

internal static class RecapNonFatalException {
    internal static bool IsCatchable(Exception exception)
        => exception is not (
            OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
        );
}
