using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atelia.Galatea.Server;

internal static class GalateaSidecarStderrDiagnostic {
    internal const int MaximumLineBytes = 8_192;
    internal const int MaximumDiagnostics = 32;

    // stderr is untrusted. Retain only understood structured failure facts;
    // never forward free text, exception messages, paths or stderr_tail.
    internal static async Task<long> DrainAsync(
        Stream stream, int generation, Action<bool, string> report
    ) {
        byte[] buffer = new byte[4096];
        byte[] line = new byte[MaximumLineBytes];
        int length = 0, emitted = 0;
        bool overflow = false;
        long total = 0;
        void Emit() {
            if (!overflow && length > 0 && emitted < MaximumDiagnostics
                && Format(line.AsMemory(0, length), generation) is { } diagnostic) {
                emitted++;
                report(diagnostic.IsError, diagnostic.Text);
            }
            length = 0;
            overflow = false;
        }
        while (true) {
            int read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) {
                Emit();
                return total;
            }
            total = Math.Min(long.MaxValue - read, total) + read;
            for (int index = 0; index < read; index++) {
                byte value = buffer[index];
                if (value == (byte)'\n') { Emit(); }
                else if (length < line.Length) { line[length++] = value; }
                else { overflow = true; }
            }
        }
    }

    private static (bool IsError, string Text)? Format(ReadOnlyMemory<byte> line, int generation) {
        try {
            using JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return null; }
            string? level = ReadString(root, "level");
            string? sourceEvent = ReadString(root, "event");
            if (level is not ("warning" or "error") || sourceEvent is not (
                "codex_process_exit" or "codex_version_mismatch"
                or "galatea_durable_sidecar_failed" or "galatea_durable_transport_failed"
                or "galatea_durable_operation_failed")) { return null; }
            var fields = new Dictionary<string, object?> {
                ["event"] = "sidecar-stderr", ["generation"] = generation,
                ["sidecarEvent"] = sourceEvent,
            };
            string? code = ReadString(root, "error_code") ?? ReadString(root, "code");
            if (code is not null) {
                fields["code"] = code is "CODEX_NOT_FOUND" or "CODEX_VERSION_MISMATCH"
                    or "CODEX_NOT_AUTHENTICATED" or "CODEX_START_FAILED" or "CODEX_PROTOCOL_ERROR"
                    or "THREAD_NOT_FOUND" or "TURN_FAILED" or "TURN_TIMEOUT" or "INVALID_CWD"
                    or "CWD_NOT_ALLOWED" or "CWD_MISMATCH" or "SANDBOX_DENIED" or "NETWORK_DENIED"
                    or "BRIDGE_BUSY" or "INVALID_CONFIG" ? code : "unrecognized";
            }
            if (ReadString(root, "rpc_method") is string method && method is
                "initialize" or "account/read" or "thread/start" or "thread/name/set"
                or "thread/read" or "thread/resume" or "thread/turns/list" or "turn/start") {
                fields["rpcMethod"] = method;
            }
            if (root.TryGetProperty("rpc_code", out JsonElement rpcCode)
                && rpcCode.ValueKind == JsonValueKind.Number && rpcCode.TryGetInt32(out int number)) {
                fields["rpcCode"] = number;
            }
            if (ReadString(root, "reason") == "workspace-routing-unauthorized") {
                fields["reason"] = "workspace-routing-unauthorized";
            }
            foreach (string key in new[] { "expected_version", "actual_version" }) {
                if (ReadString(root, key) is { } version) {
                    fields[key] = version.Length <= 17 && Regex.IsMatch(version, @"\A[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}\z")
                        ? version : "unrecognized";
                }
            }
            return (level == "error", JsonSerializer.Serialize(fields));
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
