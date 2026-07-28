using System.Runtime.CompilerServices;

namespace Atelia.SessionJournal.DerivedMemory;

internal static class DerivedMemoryEngineReadGate {
    private static readonly ConditionalWeakTable<
        SessionJournalEngine,
        object
    > Gates = new();

    public static T Run<T>(
        SessionJournalEngine engine,
        Func<T> action
    ) {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(action);
        object gate = Gates.GetValue(
            engine,
            static _ => new object()
        );
        lock (gate) {
            return action();
        }
    }
}
