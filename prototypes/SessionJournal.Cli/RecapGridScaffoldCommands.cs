using System.Security.Cryptography;
using Atelia.SessionJournal.RecapGrid;
using Atelia.SessionJournal.RecapGrid.AgentControl;
using Atelia.SessionJournal.RecapGrid.Control;
using Atelia.SessionJournal.RecapGrid.Hosting;
using Atelia.SessionJournal.RecapGrid.Runtime;

namespace Atelia.SessionJournal.Cli;

internal static partial class RecapGridCommands {
    private const int MaximumScaffoldPermissionCount = 6;

    private static int Scaffold(CliOptions options) {
        options.EnsureOnly(
            "asset",
            "profile-id",
            "connection-id",
            "semantic-model-id",
            "permission",
            "logical-column-prefix",
            "max-bootstrap-rows",
            "max-projected-calls",
            "max-concurrency",
            "dispatch-timeout-ms",
            "max-output-tokens",
            "admission-output",
            "profile-output",
            "route-output"
        );
        string assetId = options.RequireSingle("asset");
        if (!RecapGridAgentControlBuiltIns.TryCreateRegistrationBundle(
                assetId,
                out RecapGridControlRegistrationBundle? bundle)
            || bundle is null) {
            return Print(
                "scaffold",
                "built-in-asset-absent",
                new { assetId },
                2
            );
        }

        RecapGridControlPermission permissions = ParsePermissions(
            options.RequireRepeated("permission")
        );
        IReadOnlyList<string> prefixes = options.RequireRepeated(
            "logical-column-prefix"
        );
        int maximumBootstrapRows = RequireNonNegativeInt32(
            options,
            "max-bootstrap-rows"
        );
        int maximumProjectedCalls = RequireNonNegativeInt32(
            options,
            "max-projected-calls"
        );
        int maximumConcurrency = RequirePositiveInt(
            options,
            "max-concurrency"
        );
        int dispatchTimeoutMilliseconds = RequirePositiveInt(
            options,
            "dispatch-timeout-ms"
        );
        int maximumOutputTokens = RequirePositiveInt(
            options,
            "max-output-tokens"
        );
        string? requestedSemanticModel = options.GetOptionalSingle(
            "semantic-model-id"
        );

        FamilyDefinitionDigest[] familyDigests = [.. bundle.Families
            .Select(static family => family.Digest)
            .Distinct()
            .OrderBy(static digest => digest.Value, StringComparer.Ordinal)];
        string[] capabilities = [.. bundle.Definitions
            .Select(static definition =>
                definition.Capability.CapabilityFingerprint)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        ContextHeaderCarrier[] carriers = [.. bundle.Definitions
            .Select(static definition => definition.Target.Carrier)
            .Distinct()
            .Order()];
        var admission = new RecapGridControlAdmission(
            permissions,
            familyDigests,
            capabilities,
            carriers,
            prefixes,
            maximumBootstrapRows,
            maximumProjectedCalls
        );
        var profile = RecapGridAgentControlProfile.Create(
            options.RequireSingle("profile-id"),
            admission
        );

        RecapCompletionRouteKey[] exactKeys = [.. bundle.Definitions
            .Select(static definition => new RecapCompletionRouteKey(
                definition.FamilyDigest,
                definition.Capability.RuntimeProtocolId,
                definition.Capability.SemanticModelId
            ))
            .Distinct()
            .OrderBy(static key => key.FamilyDigest.Value,
                StringComparer.Ordinal)
            .ThenBy(static key => key.RuntimeProtocolId,
                StringComparer.Ordinal)
            .ThenBy(static key => key.SemanticModelId is null ? 0 : 1)
            .ThenBy(static key => key.SemanticModelId,
                StringComparer.Ordinal)];
        if (requestedSemanticModel is not null
            && exactKeys.Any(key => !string.Equals(
                key.SemanticModelId,
                requestedSemanticModel,
                StringComparison.Ordinal))) {
            throw new ArgumentException(
                "--semantic-model-id must equal the code-owned asset's exact capability; omit it for explicit null."
            );
        }
        if (requestedSemanticModel is null
            && exactKeys.Any(static key => key.SemanticModelId is not null)) {
            throw new ArgumentException(
                "--semantic-model-id is required by the code-owned asset's exact capability."
            );
        }
        string connectionId = options.RequireSingle("connection-id");
        RecapGridRouteManifest route = RecapGridRouteManifest.Create(
            exactKeys.Select(key => new RecapGridRouteManifestEntry(
                key,
                connectionId,
                maximumConcurrency,
                TimeSpan.FromMilliseconds(dispatchTimeoutMilliseconds),
                maximumOutputTokens
            ))
        );

        byte[] admissionBytes = admission.ToCanonicalBytes();
        byte[] profileBytes = profile.ToCanonicalBytes();
        byte[] routeBytes = route.ToCanonicalBytes();
        RequireExactScaffoldRoundTrip(
            admissionBytes,
            static bytes => RecapGridControlAdmission.DecodeCanonical(bytes)
                .ToCanonicalBytes(),
            "Control admission"
        );
        RequireExactScaffoldRoundTrip(
            profileBytes,
            static bytes => RecapGridAgentControlProfile.DecodeCanonical(bytes)
                .ToCanonicalBytes(),
            "Agent Control profile"
        );
        RequireExactScaffoldRoundTrip(
            routeBytes,
            static bytes => RecapGridRouteManifest.DecodeCanonical(bytes)
                .ToCanonicalBytes(),
            "route manifest"
        );

        string admissionOutput = Path.GetFullPath(
            options.RequireSingle("admission-output")
        );
        string profileOutput = Path.GetFullPath(
            options.RequireSingle("profile-output")
        );
        string routeOutput = Path.GetFullPath(
            options.RequireSingle("route-output")
        );
        RequireDistinctCreateOnlyScaffoldOutputs([
            admissionOutput,
            profileOutput,
            routeOutput
        ]);

        WriteExternalCreateNew(admissionOutput, admissionBytes);
        VerifyWrittenScaffoldFile(
            admissionOutput,
            admissionBytes,
            static bytes => RecapGridControlAdmission.DecodeCanonical(bytes)
                .ToCanonicalBytes(),
            "Control admission"
        );
        WriteExternalCreateNew(profileOutput, profileBytes);
        VerifyWrittenScaffoldFile(
            profileOutput,
            profileBytes,
            static bytes => RecapGridAgentControlProfile.DecodeCanonical(bytes)
                .ToCanonicalBytes(),
            "Agent Control profile"
        );
        WriteExternalCreateNew(routeOutput, routeBytes);
        VerifyWrittenScaffoldFile(
            routeOutput,
            routeBytes,
            static bytes => RecapGridRouteManifest.DecodeCanonical(bytes)
                .ToCanonicalBytes(),
            "route manifest"
        );

        return Print(
            "scaffold",
            "created",
            new {
                assetId,
                profileId = profile.ProfileId,
                runtimeIdentity = profile.RuntimeIdentity,
                routeCount = route.Routes.Count,
                admission = DescribeScaffoldOutput(
                    admissionOutput,
                    admissionBytes
                ),
                profile = DescribeScaffoldOutput(
                    profileOutput,
                    profileBytes
                ),
                route = DescribeScaffoldOutput(routeOutput, routeBytes)
            }
        );
    }

    private static RecapGridControlPermission ParsePermissions(
        IReadOnlyList<string> values
    ) {
        if (values.Count > MaximumScaffoldPermissionCount
            || values.Distinct(StringComparer.Ordinal).Count()
                != values.Count) {
            throw new ArgumentException(
                "--permission must be an exact unique bounded set."
            );
        }
        RecapGridControlPermission result = RecapGridControlPermission.None;
        foreach (string value in values) {
            result |= value switch {
                "create" => RecapGridControlPermission.Create,
                "register-family" =>
                    RecapGridControlPermission.RegisterFamily,
                "register-definition" =>
                    RecapGridControlPermission.RegisterDefinition,
                "register-recipe" =>
                    RecapGridControlPermission.RegisterRecipe,
                "activate" => RecapGridControlPermission.Activate,
                "promote" => RecapGridControlPermission.Promote,
                _ => throw new ArgumentException(
                    $"Unknown --permission value '{value}'."
                )
            };
        }
        return result;
    }

    private static int RequireNonNegativeInt32(
        CliOptions options,
        string key
    ) {
        string value = options.RequireSingle(key);
        return int.TryParse(value, out int parsed) && parsed >= 0
            ? parsed
            : throw new ArgumentException(
                $"--{key} must be a non-negative integer."
            );
    }

    private static void RequireDistinctCreateOnlyScaffoldOutputs(
        IReadOnlyList<string> paths
    ) {
        for (int index = 0; index < paths.Count; index++) {
            CliIo.EnsurePathChainHasNoReparsePoint(
                paths[index],
                "scaffold output"
            );
            if (File.Exists(paths[index]) || Directory.Exists(paths[index])) {
                throw new ArgumentException(
                    "Scaffold outputs are create-only and every output must be absent."
                );
            }
            for (int prior = 0; prior < index; prior++) {
                CliIo.EnsurePathsDoNotNest(
                    paths[prior],
                    paths[index],
                    "Scaffold output paths must be pairwise distinct and non-nesting."
                );
            }
        }
    }

    private static void RequireExactScaffoldRoundTrip(
        byte[] bytes,
        Func<byte[], byte[]> decodeAndEncode,
        string kind
    ) {
        byte[] canonical = decodeAndEncode(bytes);
        if (!bytes.AsSpan().SequenceEqual(canonical)) {
            throw new InvalidDataException(
                $"Generated {kind} failed exact canonical self-validation."
            );
        }
    }

    private static void VerifyWrittenScaffoldFile(
        string path,
        byte[] expected,
        Func<byte[], byte[]> decodeAndEncode,
        string kind
    ) {
        byte[] observed = File.ReadAllBytes(path);
        if (!observed.AsSpan().SequenceEqual(expected)) {
            throw new IOException($"Written {kind} bytes changed.");
        }
        RequireExactScaffoldRoundTrip(observed, decodeAndEncode, kind);
    }

    private static object DescribeScaffoldOutput(string path, byte[] bytes)
        => new {
            path,
            length = bytes.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };
}
