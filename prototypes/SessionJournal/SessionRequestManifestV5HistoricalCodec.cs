using System.Text.Json;

namespace Atelia.SessionJournal;

/// <summary>
/// Strict read-only decoder for historical Prepared v5 bodies. This type has no encoder and the
/// decoded legacy output ceiling may be consumed only by historical commitment verification.
/// </summary>
internal static class SessionRequestManifestV5HistoricalCodec {
    public static HistoricalCompletionRequestPreparedV5Body Decode(
        JsonElement body
    ) {
        SessionRequestManifestCodec.RequireExactProperties(
            body,
            "historical completion-request-prepared v5 body",
            "origin",
            "execution",
            "plan",
            "setups",
            "parameters",
            "toolSet",
            "recipe",
            "target",
            "commitment"
        );
        HistoricalSessionRequestParametersV5 parameters = ReadParameters(
            SessionRequestManifestCodec.ReadRequiredObject(body, "parameters")
        );
        var result = new HistoricalCompletionRequestPreparedV5Body(
            SessionRequestManifestCodec.ReadOrigin(
                SessionRequestManifestCodec.ReadRequiredObject(body, "origin")
            ),
            SessionRequestManifestCodec.ReadExecution(
                SessionRequestManifestCodec.ReadRequiredObject(body, "execution")
            ),
            SessionRequestManifestCodec.ReadPlan(
                SessionRequestManifestCodec.ReadRequiredObject(body, "plan")
            ),
            SessionRequestManifestCodec.ReadSetups(
                SessionRequestManifestCodec.ReadRequiredObject(body, "setups")
            ),
            parameters,
            SessionRequestManifestCodec.ReadToolSet(
                SessionRequestManifestCodec.ReadRequiredObject(body, "toolSet")
            ),
            SessionRequestManifestCodec.ReadRecipe(
                SessionRequestManifestCodec.ReadRequiredObject(body, "recipe")
            ),
            SessionRequestManifestCodec.ReadTarget(
                SessionRequestManifestCodec.ReadRequiredObject(body, "target")
            ),
            SessionRequestManifestCodec.ReadCommitment(
                SessionRequestManifestCodec.ReadRequiredObject(body, "commitment")
            )
        );
        SessionRequestManifestCodec.ValidateHistoricalV5(result);
        return result;
    }

    private static HistoricalSessionRequestParametersV5 ReadParameters(
        JsonElement element
    ) {
        SessionRequestManifestCodec.RequireExactProperties(
            element,
            "historical v5 parameters",
            "modelId",
            "maxTokens"
        );
        return new HistoricalSessionRequestParametersV5(
            SessionRequestManifestCodec.ReadRequiredString(element, "modelId"),
            SessionRequestManifestCodec.ReadNullableInt32(element, "maxTokens")
        );
    }
}
