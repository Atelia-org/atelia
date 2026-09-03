using System.Text;
using System.Text.Json;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.Tests;

internal static class HistoricalPreparedV5TestFixture {
    public static HistoricalCompletionRequestPreparedV5Body FromCurrent(
        CompletionRequestPreparedBody current,
        CompletionRequest request,
        int? legacyMaxTokens
    ) {
        byte[] canonicalBytes = SessionRequestV5HistoricalCanonicalizer.Canonicalize(
            request.ModelId,
            request.PromptPrefix,
            request.TailMessages,
            legacyMaxTokens
        );
        return new HistoricalCompletionRequestPreparedV5Body(
            current.Origin,
            current.Execution,
            current.Plan,
            current.Setups,
            new HistoricalSessionRequestParametersV5(
                current.Parameters.ModelId,
                legacyMaxTokens
            ),
            current.ToolSet,
            current.Recipe with {
                CanonicalRequestCodecId =
                    SessionRequestManifestDefaults.HistoricalCanonicalRequestCodecIdV1
            },
            current.Target,
            new SessionRequestCommitment(
                canonicalBytes.Length,
                SessionRequestCanonicalizer.Sha256Hex(canonicalBytes)
            )
        );
    }

    public static byte[] Encode(
        HistoricalCompletionRequestPreparedV5Body historical
    ) {
        ArgumentNullException.ThrowIfNull(historical);
        var currentCarrier = new CompletionRequestPreparedBody(
            historical.Origin,
            historical.Execution,
            historical.Plan,
            historical.Setups,
            new SessionRequestParameters(historical.Parameters.ModelId),
            historical.ToolSet,
            historical.Recipe with {
                CanonicalRequestCodecId =
                    SessionRequestManifestDefaults.CanonicalRequestCodecId
            },
            historical.Target,
            historical.Commitment
        );
        string json = Encoding.UTF8.GetString(SessionEventCodec.Encode(
            SessionEventKind.CompletionRequestPrepared,
            currentCarrier
        ));
        json = ReplaceOnce(json, "{\"v\":7,", "{\"v\":5,");
        string modelLiteral = JsonSerializer.Serialize(
            historical.Parameters.ModelId
        );
        string maxTokensLiteral = historical.Parameters.LegacyMaxTokens
            ?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? "null";
        json = ReplaceOnce(
            json,
            $"\"parameters\":{{\"modelId\":{modelLiteral}}}",
            $"\"parameters\":{{\"modelId\":{modelLiteral},\"maxTokens\":{maxTokensLiteral}}}"
        );
        json = ReplaceOnce(
            json,
            $"\"canonicalRequestCodecId\":\"{SessionRequestManifestDefaults.CanonicalRequestCodecId}\"",
            $"\"canonicalRequestCodecId\":\"{SessionRequestManifestDefaults.HistoricalCanonicalRequestCodecIdV1}\""
        );
        byte[] encoded = Encoding.UTF8.GetBytes(json);
        HistoricalCompletionRequestPreparedV5Body decoded = Assert.IsType<
            HistoricalCompletionRequestPreparedV5Body
        >(SessionEventCodec.Decode(
            SessionEventKind.CompletionRequestPrepared,
            encoded,
            out int bodySchemaVersion
        ));
        Assert.Equal(
            SessionRequestManifestDefaults.HistoricalBodySchemaVersionV5,
            bodySchemaVersion
        );
        Assert.Equal(historical.Origin, decoded.Origin);
        Assert.Equal(historical.Execution, decoded.Execution);
        Assert.Equal(historical.Plan.RawStartExclusive, decoded.Plan.RawStartExclusive);
        Assert.Equal(historical.Plan.RawRangeSha256, decoded.Plan.RawRangeSha256);
        Assert.True(historical.Plan.ExactContextInputs.SequenceEqual(
            decoded.Plan.ExactContextInputs
        ));
        Assert.Equal(historical.Plan.RawStartSetups, decoded.Plan.RawStartSetups);
        Assert.Equal(historical.Setups, decoded.Setups);
        Assert.Equal(historical.Parameters, decoded.Parameters);
        Assert.Equal(historical.ToolSet.Sha256, decoded.ToolSet.Sha256);
        Assert.True(historical.ToolSet.Definitions.SequenceEqual(
            decoded.ToolSet.Definitions
        ));
        Assert.Equal(historical.ToolSet.RuntimeIdentity, decoded.ToolSet.RuntimeIdentity);
        Assert.Equal(historical.Recipe, decoded.Recipe);
        Assert.Equal(historical.Target, decoded.Target);
        Assert.Equal(historical.Commitment, decoded.Commitment);
        return encoded;
    }

    private static string ReplaceOnce(
        string source,
        string marker,
        string replacement
    ) {
        int index = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Fixture marker '{marker}' was not found.");
        Assert.Equal(
            -1,
            source.IndexOf(marker, index + marker.Length, StringComparison.Ordinal)
        );
        return string.Concat(
            source.AsSpan(0, index),
            replacement,
            source.AsSpan(index + marker.Length)
        );
    }
}
