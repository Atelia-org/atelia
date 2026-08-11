using Atelia.SessionJournal.Cli;

namespace Atelia.SessionJournal.Cli.LegacyRoot.CrashHarness;

public static class CrashHarnessMarker { }

internal static class Program {
    public static int Main(string[] args) {
        if (args.Length < 2) {
            throw new ArgumentException(
                "Expected a crash mode followed by CLI arguments."
            );
        }
        string mode = args[0];
        string[] command = args[1..];
        if (mode.StartsWith("archive:", StringComparison.Ordinal)) {
            string expectedStage = mode["archive:".Length..];
            RecapGridCommands.LegacyArchiveStageForTest.Value =
                (stage, _) => {
                    if (string.Equals(
                            stage,
                            expectedStage,
                            StringComparison.Ordinal)) {
                        Environment.FailFast(
                            $"legacy archive crash at {stage}"
                        );
                    }
                };
        }
        else if (mode.StartsWith("delete:", StringComparison.Ordinal)) {
            int expectedCount = int.Parse(
                mode["delete:".Length..],
                System.Globalization.CultureInfo.InvariantCulture
            );
            RecapGridCommands.LegacyDeleteAfterFileForTest.Value = count => {
                if (count == expectedCount) {
                    Environment.FailFast(
                        $"legacy delete crash after file {count}"
                    );
                }
            };
        }
        else {
            throw new ArgumentException($"Unknown crash mode '{mode}'.");
        }
        return Atelia.SessionJournal.Cli.Program.Main(command);
    }
}
