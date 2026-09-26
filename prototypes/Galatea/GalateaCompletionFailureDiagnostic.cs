using Atelia.Completion.Abstractions;

namespace Atelia.Galatea.Server;

internal static class GalateaCompletionFailureDiagnostic {
    // Provider codes are remote strings. Only fixed, understood values may
    // enter content-free Warning logs or shared extraction diagnostics.
    internal static string? SafeProviderCode(string? code) => code switch {
        null => null,
        "rate_limit_exceeded" or "rate_limit_error" or "overloaded_error"
            or "overloaded" or "server_error" or "internal_error"
            or "internal_server_error" or "temporarily_unavailable"
            or "UNAVAILABLE" or "INTERNAL" => code,
        _ => "unrecognized",
    };

    internal static string Format(CompletionFailureInfo failure) =>
        $"failureKind={failure.Kind}, httpStatusCode={failure.HttpStatusCode?.ToString() ?? "none"}, "
        + $"providerCode={SafeProviderCode(failure.ProviderCode) ?? "none"}";
}
