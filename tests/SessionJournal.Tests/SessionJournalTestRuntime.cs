namespace Atelia.SessionJournal.Tests;

internal static class SessionJournalTestRuntime {
    internal static SessionJournalEngine Attach(
        SessionJournalEngine engine,
        SessionRuntime runtime
    ) {
        try {
            engine.UseRuntime(runtime);
            return engine;
        }
        catch {
            engine.Dispose();
            throw;
        }
    }
}
