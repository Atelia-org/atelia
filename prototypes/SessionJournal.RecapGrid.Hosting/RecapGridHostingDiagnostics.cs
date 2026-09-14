using System.Text;

namespace Atelia.SessionJournal.RecapGrid.Hosting;

internal static class RecapGridHostingDiagnostics {
    // Match the runtime's existing diagnostic budget also for provider-free
    // route inspection, which returns before runtime normalization.
    private const int MaximumDetailUtf8Bytes = 4 * 1024;

    internal static string DescribeException(Exception exception) {
        var detail = new StringBuilder();
        int remaining = MaximumDetailUtf8Bytes;
        for (Exception? current = exception; current is not null; current = current.InnerException) {
            string prefix = detail.Length == 0 ? "" : " --> ";
            foreach (string part in new[] { prefix, current.GetType().Name, ": ", current.Message }) {
                foreach (Rune rune in part.EnumerateRunes()) {
                    if (rune.Utf8SequenceLength > remaining) { return detail.ToString(); }
                    detail.Append(rune.ToString());
                    remaining -= rune.Utf8SequenceLength;
                }
            }
        }
        return detail.ToString();
    }
}
