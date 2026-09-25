using Xunit;

namespace Atelia.Testing;

public sealed class CapacityFactAttribute : FactAttribute {
    public const string RunGate = "ATELIA_RUN_CAPACITY_TESTS";

    public CapacityFactAttribute() {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RunGate),
                "1",
                StringComparison.Ordinal)) {
            Skip = $"Set {RunGate}=1 to run capacity tests.";
        }
    }
}
