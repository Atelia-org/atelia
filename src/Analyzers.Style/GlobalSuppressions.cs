// This file can hold suppressions for diagnostics inside the analyzer assembly itself
using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Analyzer infrastructure typical pattern")]
