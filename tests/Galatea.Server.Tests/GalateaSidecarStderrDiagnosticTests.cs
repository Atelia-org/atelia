using System.Text;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaSidecarStderrDiagnosticTests {
    [Fact]
    public async Task StartupFailureRetainsRpcFactsAndDropsUntrustedContentIncludingFinalLineWithoutNewline() {
        const string input = """
            raw exception with secret /private/path
            {"level":"info","event":"codex_started","pid":123}
            {"level":"error","event":"galatea_durable_sidecar_failed","error_code":"CODEX_PROTOCOL_ERROR","rpc_method":"account/read","rpc_code":-32603,"reason":"workspace-routing-unauthorized","message":"secret","stderr_tail":"/private/path","stack":"secret"}
            """;
        var diagnostics = new List<(bool IsError, string Text)>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        long bytes = await GalateaSidecarStderrDiagnostic.DrainAsync(stream, 7,
            (isError, text) => diagnostics.Add((isError, text)));
        Assert.Equal(Encoding.UTF8.GetByteCount(input), bytes);
        var diagnostic = Assert.Single(diagnostics);
        Assert.True(diagnostic.IsError);
        Assert.Contains("\"generation\":7", diagnostic.Text);
        Assert.Contains("CODEX_PROTOCOL_ERROR", diagnostic.Text);
        Assert.Contains("\"rpcMethod\":\"account/read\"", diagnostic.Text);
        Assert.Contains("\"rpcCode\":-32603", diagnostic.Text);
        Assert.Contains("workspace-routing-unauthorized", diagnostic.Text);
        Assert.DoesNotContain("secret", diagnostic.Text);
        Assert.DoesNotContain("private", diagnostic.Text);
    }

    [Fact]
    public async Task OversizedOrMalformedLinesDoNotHideLaterFailuresAndOutputIsBounded() {
        const string failure = "{\"level\":\"warning\",\"event\":\"codex_process_exit\",\"code\":\"CODEX_START_FAILED\"}\n";
        string input = new string('x', GalateaSidecarStderrDiagnostic.MaximumLineBytes + 1)
            + "\n{invalid}\n[]\n" + string.Concat(Enumerable.Repeat(failure, 50));
        var diagnostics = new List<string>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        long bytes = await GalateaSidecarStderrDiagnostic.DrainAsync(stream, 1, (isError, text) => {
            Assert.False(isError);
            diagnostics.Add(text);
        });
        Assert.Equal(Encoding.UTF8.GetByteCount(input), bytes);
        Assert.Equal(GalateaSidecarStderrDiagnostic.MaximumDiagnostics, diagnostics.Count);
        Assert.All(diagnostics, text => Assert.Contains("CODEX_START_FAILED", text));
    }

    [Fact]
    public async Task UnknownCodesMethodsReasonsAndVersionSuffixesCannotBecomeLogContent() {
        const string input = """
            {"level":"error","event":"secret","code":"CODEX_START_FAILED"}
            {"level":"error","event":"codex_version_mismatch","code":"secret","rpc_method":"secret","rpc_code":"secret","reason":"secret","expected_version":"0.156.1","actual_version":"0.156.1 secret"}
            """;
        var diagnostics = new List<string>();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        await GalateaSidecarStderrDiagnostic.DrainAsync(stream, 1, (_, text) => diagnostics.Add(text));
        string diagnostic = Assert.Single(diagnostics);
        Assert.Contains("0.156.1", diagnostic);
        Assert.Contains("unrecognized", diagnostic);
        Assert.DoesNotContain("secret", diagnostic);
        Assert.DoesNotContain("rpcMethod", diagnostic);
        Assert.DoesNotContain("rpcCode", diagnostic);
        Assert.DoesNotContain("reason", diagnostic);
    }
}
