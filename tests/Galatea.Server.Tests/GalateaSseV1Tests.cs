using System.Text;
using System.Text.Json;
using Atelia.Completion;
using Xunit;

namespace Atelia.Galatea.Server.Tests;

public sealed class GalateaSseV1Tests {
    [Fact]
    public void Frames_AreExactUtf8LfAndClosedTypedPayloads() {
        AssertFrame(
            GalateaSseFrames.Status(GalateaSseStatusCode.Generating),
            "event: status\ndata: {\"code\":\"generating\"}\n\n"
        );
        AssertFrame(
            GalateaSseFrames.Status(
                GalateaSseStatusCode.InputNormalizationFinished,
                changed: true
            ),
            "event: status\ndata: {\"code\":\"input-normalization-finished\",\"changed\":true}\n\n"
        );
        AssertFrame(
            GalateaSseFrames.ReasoningDelta("reason"),
            "event: reasoning-delta\ndata: {\"delta\":\"reason\"}\n\n"
        );
        AssertFrame(
            GalateaSseFrames.TextDelta("text"),
            "event: text-delta\ndata: {\"delta\":\"text\"}\n\n"
        );
        AssertFrame(
            GalateaSseFrames.Done(recent: null),
            "event: done\ndata: {\"recent\":null}\n\n"
        );
        AssertFrame(
            GalateaSseFrames.Error(GalateaSseErrorCode.InternalFailure),
            "event: error\ndata: {\"code\":\"internal-failure\",\"message\":\""
            + "\\u751F\\u6210\\u8FC7\\u7A0B\\u4E2D\\u53D1\\u751F"
            + "\\u5185\\u90E8\\u9519\\u8BEF\\uFF0C\\u8BF7\\u5237"
            + "\\u65B0\\u540E\\u91CD\\u8BD5\\u3002\"}\n\n"
        );
        Assert.Throws<ArgumentException>(() =>
            GalateaSseFrames.Status(
                GalateaSseStatusCode.Generating,
                changed: false
            )
        );
        Assert.Throws<ArgumentNullException>(() =>
            GalateaSseFrames.Status(
                GalateaSseStatusCode.InputNormalizationFinished
            )
        );
        Assert.Throws<ArgumentException>(() =>
            GalateaSseFrames.TextDelta(string.Empty)
        );

        string boundaryDelta = new string('x', 511)
            + "\U0001F600\0你好<>&\"\\\n";
        GalateaSseFrame escaped = GalateaSseFrames.TextDelta(
            boundaryDelta
        );
        string expectedJson = JsonSerializer.Serialize(
            new { delta = boundaryDelta },
            GalateaJson.Options
        );
        Assert.Equal(
            $"event: text-delta\ndata: {expectedJson}\n\n",
            FrameText(escaped)
        );
    }

    [Fact]
    public void ErrorFramesExposeOnlyClosedCodeAndSanitizedMessage() {
        string[] expectedCodes = [
            "operator-stop",
            "server-shutdown",
            "completion-failed",
            "turn-unavailable",
            "internal-failure"
        ];
        GalateaSseErrorCode[] values =
            Enum.GetValues<GalateaSseErrorCode>();
        Assert.Equal(expectedCodes.Length, values.Length);
        foreach (GalateaSseErrorCode value in values) {
            GalateaSseFrame frame = GalateaSseFrames.Error(value);
            string text = FrameText(frame);
            string dataLine = text.Split('\n')[1];
            using JsonDocument document = JsonDocument.Parse(
                dataLine["data: ".Length..]
            );
            JsonElement payload = document.RootElement;
            Assert.Equal(
                ["code", "message"],
                payload.EnumerateObject()
                    .Select(static property => property.Name)
                    .Order()
            );
            Assert.Contains(
                payload.GetProperty("code").GetString(),
                expectedCodes
            );
            Assert.False(string.IsNullOrWhiteSpace(
                payload.GetProperty("message").GetString()
            ));
            Assert.DoesNotContain(
                "failureReason",
                text,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain(
                "provider",
                text,
                StringComparison.OrdinalIgnoreCase
            );
        }
    }

    [Fact]
    public async Task Sequencer_LinearizesReplayAndLiveWithOneTerminal() {
        GalateaLiveTurn turn = Turn();
        using GalateaTurnSubscription live = turn.Subscribe();
        Task[] publishers = Enumerable.Range(0, 100)
            .Select(index => Task.Run(() =>
                turn.PublishTextDelta($"delta-{index}")
            ))
            .ToArray();

        await Task.WhenAll(publishers);
        turn.PublishDone(recent: null);
        turn.Complete();

        List<GalateaSseFrame> liveFrames = [];
        await foreach (GalateaSseFrame frame in live.Reader.ReadAllAsync()) {
            liveFrames.Add(frame);
        }
        using GalateaTurnSubscription replay = turn.Subscribe();
        Assert.Equal(
            replay.ReplayFrames.Select(FrameText),
            liveFrames.Select(FrameText)
        );
        Assert.Equal(101, liveFrames.Count);
        Assert.Equal("done", liveFrames[^1].EventName);
        Assert.Single(liveFrames, static frame => frame.IsTerminal);
        Assert.Equal("completed", turn.Status);

        Assert.Throws<InvalidOperationException>(() =>
            turn.PublishDone(recent: null)
        );
        Assert.Throws<InvalidOperationException>(() =>
            turn.PublishError(GalateaSseErrorCode.InternalFailure)
        );
        Assert.Throws<InvalidOperationException>(() =>
            turn.PublishTextDelta("post-terminal")
        );
        Assert.Throws<InvalidOperationException>(() => Turn().Complete());
    }

    [Fact]
    public async Task ConcurrentTerminalPublicationHasExactlyOneWinner() {
        GalateaLiveTurn turn = Turn();
        using var start = new ManualResetEventSlim();
        Task<Exception?> done = Task.Run(() => AttemptTerminal(
            start,
            () => turn.PublishDone(recent: null)
        ));
        Task<Exception?> error = Task.Run(() => AttemptTerminal(
            start,
            () => turn.PublishError(
                GalateaSseErrorCode.CompletionFailed
            )
        ));

        start.Set();
        Exception?[] outcomes = await Task.WhenAll(done, error);
        Assert.Single(outcomes, static outcome => outcome is null);
        Assert.Single(
            outcomes,
            static outcome => outcome is InvalidOperationException
        );
        using GalateaTurnSubscription replay = turn.Subscribe();
        GalateaSseFrame terminal = Assert.Single(replay.ReplayFrames);
        Assert.True(terminal.IsTerminal);
        Assert.True(terminal.EventName is "done" or "error");
    }

    [Fact]
    public async Task SlowSubscriberOverflowDisconnectsOnlyThatSubscriber() {
        GalateaLiveTurn turn = Turn();
        using GalateaTurnSubscription slow = turn.Subscribe();
        using GalateaTurnSubscription fast = turn.Subscribe();
        var fastFrames = new List<GalateaSseFrame>();
        using var fastObserved = new SemaphoreSlim(0);
        Task fastReader = Task.Run(async () => {
            await foreach (GalateaSseFrame frame
                           in fast.Reader.ReadAllAsync()) {
                lock (fastFrames) {
                    fastFrames.Add(frame);
                }
                fastObserved.Release();
            }
        });
        for (int index = 0;
             index <= GalateaSseLimits.SubscriberChannelCapacity;
            index++) {
            turn.PublishTextDelta($"delta-{index}");
            Assert.True(await fastObserved.WaitAsync(
                TimeSpan.FromSeconds(3)
            ));
        }
        turn.PublishDone(recent: null);
        await fastReader;

        List<GalateaSseFrame> observed = [];
        await foreach (GalateaSseFrame frame in slow.Reader.ReadAllAsync()) {
            observed.Add(frame);
        }
        Assert.Equal(
            GalateaSseLimits.SubscriberChannelCapacity,
            observed.Count
        );
        Assert.DoesNotContain(observed, static frame => frame.IsTerminal);
        Assert.Equal("completed", turn.Status);
        Assert.False(turn.StopRequested);

        lock (fastFrames) {
            Assert.Equal(
                GalateaSseLimits.SubscriberChannelCapacity + 2,
                fastFrames.Count
            );
            Assert.Equal("done", fastFrames[^1].EventName);
            Assert.Single(
                fastFrames,
                static frame => frame.IsTerminal
            );
        }

        using GalateaTurnSubscription replay = turn.Subscribe();
        Assert.Equal(
            GalateaSseLimits.SubscriberChannelCapacity + 2,
            replay.ReplayFrames.Count
        );
        Assert.Equal("done", replay.ReplayFrames[^1].EventName);
    }

    [Fact]
    public void HugeHighEscapePreviewIsCappedBeforeMaterialization() {
        string huge = new('\0', 100 * 1024 * 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();

        GalateaSseFrame? frame = GalateaSseFrames.TryTextDelta(
            huge,
            maximumFrameUtf8Bytes: 4 * 1024
        );

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Null(frame);
        Assert.True(
            allocated < 1024 * 1024,
            $"Capped preview encoding allocated {allocated} bytes."
        );

        GalateaLiveTurn turn = Turn();
        long productionBefore = GC.GetAllocatedBytesForCurrentThread();
        turn.PublishTextDelta(huge);
        long productionAllocated =
            GC.GetAllocatedBytesForCurrentThread() - productionBefore;
        Assert.True(turn.PreviewSuppressed);
        Assert.True(
            productionAllocated
                < GalateaSseLimits.MaximumPreviewUtf8Bytes
                    + 1024 * 1024,
            $"Production preview gate allocated {productionAllocated} bytes."
        );
        turn.PublishDone(recent: null);
        using GalateaTurnSubscription replay = turn.Subscribe();
        Assert.Single(replay.ReplayFrames);
        Assert.Equal("done", replay.ReplayFrames[0].EventName);
    }

    [Fact]
    public void PreviewByteCapSuppressesWithoutChangingDurableOutcome() {
        GalateaSseFrame one = GalateaSseFrames.TextDelta("x");
        int fixedBytes = one.Utf8Length - 1;
        string exactPayload = new(
            'x',
            GalateaSseLimits.MaximumPreviewUtf8Bytes - fixedBytes
        );
        GalateaSseFrame exact = GalateaSseFrames.TextDelta(exactPayload);
        Assert.Equal(
            GalateaSseLimits.MaximumPreviewUtf8Bytes,
            exact.Utf8Length
        );

        GalateaLiveTurn turn = Turn();
        turn.PublishTextDelta(exactPayload);
        turn.PublishStatus(GalateaSseStatusCode.UsingTools);
        turn.PublishTextDelta(string.Empty);
        turn.PublishDone(recent: null);

        Assert.True(turn.PreviewSuppressed);
        Assert.False(turn.StopRequested);
        Assert.Equal("completed", turn.Status);
        using GalateaTurnSubscription replay = turn.Subscribe();
        Assert.Equal(2, replay.ReplayFrames.Count);
        Assert.Equal("text-delta", replay.ReplayFrames[0].EventName);
        Assert.Equal("done", replay.ReplayFrames[1].EventName);
        Assert.True(
            turn.ReplayUtf8Bytes
            <= GalateaSseLimits.MaximumWholeReplayUtf8Bytes
        );
    }

    [Fact]
    public void PreviewEventCapReservesExactlyOneTerminal() {
        GalateaLiveTurn turn = Turn();
        for (int index = 0;
             index < GalateaSseLimits.MaximumPreviewEventCount;
             index++) {
            turn.PublishTextDelta("x");
        }
        turn.PublishTextDelta("suppressed");
        turn.PublishError(GalateaSseErrorCode.CompletionFailed);

        Assert.True(turn.PreviewSuppressed);
        using GalateaTurnSubscription replay = turn.Subscribe();
        Assert.Equal(
            GalateaSseLimits.MaximumReplayEventCount,
            replay.ReplayFrames.Count
        );
        Assert.Equal("error", replay.ReplayFrames[^1].EventName);
        Assert.Single(replay.ReplayFrames, static frame => frame.IsTerminal);
    }

    [Fact]
    public void LimitsComposeAndMaximumRecentDoneFitsTerminalReserve() {
        Assert.Equal(
            GalateaSseLimits.MaximumWholeReplayUtf8Bytes,
            GalateaSseLimits.MaximumPreviewUtf8Bytes
                + GalateaSseLimits.MaximumTerminalFrameUtf8Bytes
        );
        Assert.Equal(
            GalateaSseLimits.MaximumReplayEventCount,
            GalateaSseLimits.MaximumPreviewEventCount + 1
        );
        Assert.Equal(
            GalateaSseLimits.MaximumWholeReplayUtf8Bytes,
            GalateaSseLimits.BrowserMaximumConnectionBytes
        );
        Assert.Equal(
            GalateaSseLimits.MaximumTerminalFrameUtf8Bytes,
            GalateaSseLimits.BrowserMaximumFrameBytes
        );
        Assert.Equal(
            GalateaSseLimits.MaximumPreviewUtf8Bytes,
            GalateaHostService.MaximumRecentResponseUtf8Bytes
        );

        RecentTurnsResponseDto empty = RecentWithAssistantText(string.Empty);
        int emptyBytes = JsonSerializer.SerializeToUtf8Bytes(
            empty,
            GalateaJson.Options
        ).Length;
        RecentTurnsResponseDto maximum = RecentWithAssistantText(
            new string(
                'x',
                GalateaHostService.MaximumRecentResponseUtf8Bytes
                    - emptyBytes
            )
        );
        Assert.Equal(
            GalateaHostService.MaximumRecentResponseUtf8Bytes,
            JsonSerializer.SerializeToUtf8Bytes(
                maximum,
                GalateaJson.Options
            ).Length
        );
        GalateaSseFrame done = GalateaSseFrames.Done(maximum);
        Assert.True(
            done.Utf8Length
            < GalateaSseLimits.MaximumTerminalFrameUtf8Bytes
        );
    }

    [Fact]
    public void TerminalFrameBoundIsInclusiveAndFailureDoesNotWinTerminal() {
        RecentTurnsResponseDto empty = RecentWithAssistantText(string.Empty);
        int fixedBytes = GalateaSseFrames.Done(empty).Utf8Length;
        RecentTurnsResponseDto exact = RecentWithAssistantText(
            new string(
                'x',
                GalateaSseLimits.MaximumTerminalFrameUtf8Bytes
                    - fixedBytes
            )
        );
        Assert.Equal(
            GalateaSseLimits.MaximumTerminalFrameUtf8Bytes,
            GalateaSseFrames.Done(exact).Utf8Length
        );
        GalateaLiveTurn accepted = Turn();
        accepted.PublishDone(exact);
        Assert.Equal("completed", accepted.Status);

        RecentTurnsResponseDto oversized = RecentWithAssistantText(
            new string(
                'x',
                GalateaSseLimits.MaximumTerminalFrameUtf8Bytes
                    - fixedBytes + 1
            )
        );
        GalateaLiveTurn rejected = Turn();
        Assert.Throws<InvalidOperationException>(() =>
            rejected.PublishDone(oversized)
        );
        Assert.Equal("running", rejected.Status);
        rejected.PublishError(GalateaSseErrorCode.InternalFailure);
        using GalateaTurnSubscription replay = rejected.Subscribe();
        Assert.Single(replay.ReplayFrames);
        Assert.Equal("error", replay.ReplayFrames[0].EventName);
    }

    [Fact]
    public void AppBootstrapOwnsBrowserLimitsAndCacheBustedModule() {
        string html = GalateaHtml.RenderAppPage(
            new GalateaUserConfig(
                "alice",
                "password",
                "/session",
                GalateaSessionProvisioning.ExistingOnly
            ),
            [new GalateaConnectionInfoDto("test", "model")],
            "test",
            maintenanceMode: false,
            assetVersion: "fixture-token"
        );

        Assert.Contains(
            $"maximumConnectionBytes: {GalateaSseLimits.BrowserMaximumConnectionBytes}",
            html,
            StringComparison.Ordinal
        );
        Assert.Contains(
            $"maximumFrameBytes: {GalateaSseLimits.BrowserMaximumFrameBytes}",
            html,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "<script type=\"module\" src=\"/assets/galatea.js?v=fixture-token\"></script>",
            html,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void KnownRecoveryAndConfigurationReasonsAreTurnUnavailable() {
        string[] reasons = [
            "stale-session-head",
            "tool-set-fingerprint-mismatch",
            .. Enum.GetValues<CompletionDispatchBindingUnavailableReason>()
                .Select(static reason => reason.ToString())
        ];

        foreach (string reason in reasons) {
            Assert.Equal(
                GalateaSseErrorCode.TurnUnavailable,
                GalateaSseErrorClassifier.Classify(
                    new GalateaTurnException("detail", reason)
                )
            );
        }
        Assert.Equal(
            GalateaSseErrorCode.CompletionFailed,
            GalateaSseErrorClassifier.Classify(
                new GalateaTurnException(
                    "provider detail",
                    "provider-finish-reason"
                )
            )
        );
    }

    private static GalateaLiveTurn Turn() => new(
        "message",
        new GalateaTurnOptions("test")
    );

    private static RecentTurnsResponseDto RecentWithAssistantText(
        string text
    ) => new(
        [new RecentTurnDto(
            "user",
            new AssistantMessageDto(text, ReasoningText: null)
        )],
        RewindLatestToken: null,
        ContextHeader: ContextHeaderDto.Empty,
        RecapGridReadiness: null
    );

    private static void AssertFrame(
        GalateaSseFrame frame,
        string expected
    ) {
        Assert.Equal(expected, FrameText(frame));
        Assert.DoesNotContain('\r', expected);
    }

    private static string FrameText(GalateaSseFrame frame) =>
        Encoding.UTF8.GetString(frame.Utf8.Span);

    private static Exception? AttemptTerminal(
        ManualResetEventSlim start,
        Action publish
    ) {
        start.Wait();
        try {
            publish();
            return null;
        }
        catch (Exception exception) {
            return exception;
        }
    }


}
