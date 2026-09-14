using Atelia.SessionJournal.RecapGrid.Runtime;

namespace Atelia.SessionJournal.RecapGrid.Hosting;

/// <summary>Live lifecycle observation with unchanged bounded settled evidence.</summary>
internal sealed class LiveRecapCompletionTelemetry(
    BoundedRecapCompletionTelemetry evidence,
    IRecapCompletionTelemetry? live
) : IRecapCompletionTelemetry {
    public void Record(RecapCompletionTelemetryEvent value) {
        if (value.Kind == "completion-settled") {
            Observe(evidence, value);
        }
        // No evidence lock is held across the external observer.
        if (live is not null) { Observe(live, value); }
    }

    private static void Observe(IRecapCompletionTelemetry sink,
        RecapCompletionTelemetryEvent value) {
        try { sink.Record(value); }
        catch (Exception exception) when (exception is not OutOfMemoryException
            and not StackOverflowException and not AccessViolationException) {
            // Neither observer is an outcome dependency of the other or runtime.
        }
    }
}
