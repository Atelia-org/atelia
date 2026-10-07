using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaImapRebaselineTests {
    private const string Secret = "SYNTHETIC_REBASELINE_PRIVATE_ARGUMENT";

    [Theory]
    [InlineData("missing-config")]
    [InlineData("relative-config")]
    [InlineData("unknown-option")]
    [InlineData("duplicate-character")]
    [InlineData("missing-option-value")]
    [InlineData("incomplete-apply")]
    [InlineData("zero-validity")]
    [InlineData("negative-revision")]
    [InlineData("overflow-uid")]
    [InlineData("preview-with-apply-values")]
    [InlineData("valid-preview-missing-file")]
    [InlineData("valid-apply-missing-file")]
    public async Task InvalidOrUnavailableOfflineInvocationFailsWithoutEchoingPrivateArgumentsOrCreatingState(string scenario) {
        string directory = Directory.CreateTempSubdirectory("atelia-imap-rebaseline-tests-").FullName;
        try {
            string missingConfig = Path.Combine(directory, Secret + ".json");
            string[] prefix = ["operator", "rebaseline-imap", "--config", missingConfig, "--character", Secret];
            string[] values = ["--expected-validity", "41", "--expected-cursor", "0",
                "--expected-revision", "0", "--new-validity", "42", "--new-uidnext", "1"];
            string[] args = scenario switch {
                "missing-config" => ["operator", "rebaseline-imap", "--character", Secret],
                "relative-config" => ["operator", "rebaseline-imap", "--config", Secret + ".json", "--character", "alice"],
                "unknown-option" => [.. prefix, "--password", Secret],
                "duplicate-character" => [.. prefix, "--character", "alice"],
                "missing-option-value" => [.. prefix, "--new-uidnext"],
                "incomplete-apply" => [.. prefix, "--apply", "--expected-validity", "41"],
                "zero-validity" => [.. prefix, "--apply", .. Change(values, "--new-validity", "0")],
                "negative-revision" => [.. prefix, "--apply", .. Change(values, "--expected-revision", "-1")],
                "overflow-uid" => [.. prefix, "--apply", .. Change(values, "--new-uidnext", "4294967296")],
                "preview-with-apply-values" => [.. prefix, .. values],
                "valid-preview-missing-file" => prefix,
                "valid-apply-missing-file" => [.. prefix, "--apply", .. values],
                _ => throw new ArgumentException("Unknown fixture scenario.", nameof(scenario))
            };
            using var output = new StringWriter();
            using var error = new StringWriter();

            int exitCode = await GalateaImapRebaseline.RunAsync(args, output, error);

            Assert.Equal(2, exitCode);
            Assert.Equal(string.Empty, output.ToString());
            Assert.StartsWith("IMAP rebaseline failed: exceptionType=", error.ToString());
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(directory, error.ToString(), StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            // Valid command shapes stop at the missing config file, before
            // any host, store or IMAP network connection can be constructed.
            if (scenario.StartsWith("valid-", StringComparison.Ordinal)) {
                Assert.Contains("System.IO.FileNotFoundException", error.ToString(), StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string[] Change(string[] arguments, string option, string value) {
        string[] changed = (string[])arguments.Clone();
        int index = Array.IndexOf(changed, option);
        Assert.True(index >= 0);
        changed[index + 1] = value;
        return changed;
    }
}
