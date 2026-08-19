using System.Collections.Immutable;
using System.Text;
using Atelia.Completion.Abstractions;
using Xunit;

namespace Atelia.SessionJournal.Tests;

public sealed class SessionSupplementalContextIntegrationTests {
    private const string SelectedPrefix =
        "{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"";
    private const string SelectedSuffix = "\"}";

    [Fact]
    public void NoMatch_HasExactCanonicalControlAndTerminalHash() {
        SessionRequestContextInput terminal =
            SessionSupplementalContextRecipe.CreateNoMatchTerminalInput();

        Assert.Equal(
            "{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}",
            terminal.ContextSnapshot.ObservationMessage
        );
        Assert.Equal("", terminal.ContextSnapshot.SystemPromptFragment);
        Assert.Equal("", terminal.ContextSnapshot.ActionMessage);
        Assert.Equal(
            SessionArtifactContextSnapshotHasher.ComputeSha256(
                terminal.ContextSnapshot
            ),
            terminal.ContentSha256
        );
        SessionSupplementalContextControl control =
            SessionSupplementalContextRecipe.ParseControl(
                terminal.ContextSnapshot.ObservationMessage
            );
        Assert.Equal(SessionSupplementalContextStatus.NoMatch, control.Status);
        Assert.Null(control.ObservationContent);
    }

    [Fact]
    public void Selected_FixedEncoderPreservesScalarsAndCanonicalizesControls() {
        const string content =
            "A\"\\\b\t\n\f\r\u0001\u007f\u0085\u2028\u2029汉😀";

        string rendered =
            SessionSupplementalContextRecipe.RenderSelectedControl(content);

        Assert.Equal(
            SelectedPrefix
                + "A\\\"\\\\\\b\\t\\n\\f\\r\\u0001\\u007f\\u0085\\u2028\\u2029汉😀"
                + SelectedSuffix,
            rendered
        );
        SessionSupplementalContextControl parsed =
            SessionSupplementalContextRecipe.ParseControl(rendered);
        Assert.Equal(SessionSupplementalContextStatus.Selected, parsed.Status);
        Assert.Equal(content, parsed.ObservationContent);
    }

    [Theory]
    [InlineData(" {\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}\n")]
    [InlineData("{\"status\":\"no-match\",\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null,\"extra\":0}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"No-Match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":\"not-null\"}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\"}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\\u0061\"}")]
    [InlineData("\ufeff{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"no-match\",\"observationContent\":null}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\u0085\"}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\\u008A\"}")]
    [InlineData("{\"schema\":\"atelia.session-journal.supplemental-context.control.v1\",\"status\":\"selected\",\"observationContent\":\"\\uD83D\\uDE00\"}")]
    public void Parser_RejectsShapeAndNonCanonicalByteGrammar(string value) {
        Assert.Throws<InvalidDataException>(
            () => SessionSupplementalContextRecipe.ParseControl(value)
        );
    }

    [Fact]
    public void EncoderAndParser_RejectInvalidUnicodeScalarData() {
        string invalid = new(['\ud800']);

        Assert.Throws<ArgumentException>(
            () => SessionSupplementalContextRecipe.RenderSelectedControl(invalid)
        );
        Assert.Throws<InvalidDataException>(
            () => SessionSupplementalContextRecipe.ParseControl(
                SelectedPrefix + invalid + SelectedSuffix
            )
        );
    }

    [Fact]
    public void Encoder_AcceptsExactSnapshotBoundAndRejectsOneByteMore() {
        int carrierOverhead = Encoding.UTF8.GetByteCount(
            SelectedPrefix + SelectedSuffix
        );
        int exactContentLength =
            SessionArtifactContextSnapshotHasher.MaxSnapshotUtf8Bytes
            - carrierOverhead;
        string exact = new('a', exactContentLength);

        string rendered =
            SessionSupplementalContextRecipe.RenderSelectedControl(exact);

        Assert.Equal(
            SessionArtifactContextSnapshotHasher.MaxSnapshotUtf8Bytes,
            Encoding.UTF8.GetByteCount(rendered)
        );
        Assert.Throws<ArgumentException>(
            () => SessionSupplementalContextRecipe.RenderSelectedControl(
                exact + "a"
            )
        );
    }

    [Fact]
    public void Expand_PreservesRecapOrderAndNeverExposesTerminalEnvelope() {
        ImmutableArray<SessionRequestContextInput> recap = [
            ContextInput(new("recap system", "", "")),
            ContextInput(new("", "recap observation", "")),
            ContextInput(new("", "", "recap action"))
        ];
        CompletionRequestPreparedBody manifest = PreparedV6Fixture.Create(
            "exact supplemental observation",
            recap
        );

        (string systemPrompt, ImmutableArray<IHistoryMessage> context) =
            SessionSupplementalContextRecipe.Expand(
                "base system",
                manifest.Plan.ExactContextInputs
            );

        Assert.Equal("base system\n\nrecap system", systemPrompt);
        Assert.Collection(
            context,
            message => Assert.Equal(
                "recap observation",
                Assert.IsType<ObservationMessage>(message).Content
            ),
            message => Assert.Equal(
                "recap action",
                Assert.IsType<ActionMessage>(message).GetFlattenedText()
            ),
            message => Assert.Equal(
                "exact supplemental observation",
                Assert.IsType<ObservationMessage>(message).Content
            )
        );
        Assert.DoesNotContain(
            context.OfType<ObservationMessage>(),
            static observation => observation.Content?.Contains(
                SessionSupplementalContextRecipe.ControlSchemaId,
                StringComparison.Ordinal
            ) == true
        );
    }

    [Fact]
    public void Expand_NoMatchAddsNoProviderFacingMessage() {
        CompletionRequestPreparedBody manifest = PreparedV6Fixture.Create(
            selectedObservationContent: null,
            recapInputs: []
        );

        (string systemPrompt, ImmutableArray<IHistoryMessage> context) =
            SessionSupplementalContextRecipe.Expand(
                "base system",
                manifest.Plan.ExactContextInputs
            );

        Assert.Equal("base system", systemPrompt);
        Assert.Empty(context);
    }

    private static SessionRequestContextInput ContextInput(
        SessionRequestArtifactContextSnapshot snapshot
    ) => new(
        SessionArtifactContextSnapshotHasher.ComputeSha256(snapshot),
        snapshot
    );
}
