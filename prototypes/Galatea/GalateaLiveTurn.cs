using System.Threading.Channels;
using Atelia.Completion.Abstractions;

namespace Atelia.Galatea.Server;

internal sealed class GalateaLiveTurn {
    private readonly object _gate = new();
    private readonly Dictionary<long, Channel<GalateaSseFrame>>
        _subscribers = new();
    private readonly List<GalateaSseFrame> _replayFrames = [];
    private long _nextSubscriberId;
    private int _previewEventCount;
    private int _previewUtf8Bytes;
    private bool _previewSuppressed;
    private bool _terminalPublished;
    private string _status = "running";
    private string? _phase;

    public GalateaLiveTurn(
        string? userMessage,
        GalateaTurnOptions options
    ) {
        TurnId = Guid.NewGuid().ToString("N");
        UserMessage = userMessage;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        StopController = new GalateaTurnStopController();
    }

    public string TurnId { get; }

    public string? UserMessage { get; }

    public GalateaTurnOptions Options { get; }

    public string Status {
        get {
            lock (_gate) {
                return _status;
            }
        }
    }

    public string? Phase {
        get {
            lock (_gate) {
                return _phase;
            }
        }
    }

    internal bool PreviewSuppressed {
        get {
            lock (_gate) {
                return _previewSuppressed;
            }
        }
    }

    internal int ReplayUtf8Bytes {
        get {
            lock (_gate) {
                return _replayFrames.Sum(
                    static frame => frame.Utf8Length
                );
            }
        }
    }

    public Task? RunTask { get; set; }

    internal GalateaTurnStopController StopController { get; }

    public CompletionStreamObserver Observer => StopController.Observer;

    public CancellationToken PreDispatchStopToken =>
        StopController.PreDispatchStopToken;

    public bool StopRequested => StopController.StopRequested;

    public GalateaTurnSubscription Subscribe() {
        lock (_gate) {
            long subscriberId = Interlocked.Increment(
                ref _nextSubscriberId
            );
            var channel = Channel.CreateBounded<GalateaSseFrame>(
                new BoundedChannelOptions(
                    GalateaSseLimits.SubscriberChannelCapacity
                ) {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait
                }
            );
            if (!_terminalPublished) {
                _subscribers.Add(subscriberId, channel);
            }
            else {
                channel.Writer.TryComplete();
            }
            return new GalateaTurnSubscription(
                this,
                subscriberId,
                _replayFrames.ToArray(),
                channel.Reader
            );
        }
    }

    internal void PublishStatus(
        GalateaSseStatusCode code,
        bool? changed = null
    ) {
        if (!ShouldEncodePreview()) {
            return;
        }
        PublishPreview(
            GalateaSseFrames.Status(code, changed),
            GalateaSseFrames.StatusCode(code)
        );
    }

    internal void PublishReasoningDelta(string delta) {
        if (!ShouldEncodePreview()) {
            return;
        }
        PublishPreview(GalateaSseFrames.ReasoningDelta(delta));
    }

    internal void PublishTextDelta(string delta) {
        if (!ShouldEncodePreview()) {
            return;
        }
        PublishPreview(GalateaSseFrames.TextDelta(delta));
    }

    internal void PublishDone(RecentTurnsResponseDto? recent) =>
        PublishTerminal(
            GalateaSseFrames.Done(recent),
            status: "completed"
        );

    internal void PublishError(GalateaSseErrorCode code) =>
        PublishTerminal(
            GalateaSseFrames.Error(code),
            status: "failed"
        );

    public void Complete() {
        lock (_gate) {
            if (!_terminalPublished) {
                throw new InvalidOperationException(
                    "A live Galatea turn cannot complete without a terminal SSE frame."
                );
            }
        }
        StopController.Complete();
    }

    public void Unsubscribe(long subscriberId) {
        Channel<GalateaSseFrame>? subscriber;
        lock (_gate) {
            _subscribers.Remove(subscriberId, out subscriber);
        }
        subscriber?.Writer.TryComplete();
    }

    public bool RequestStop() => StopController.RequestStop();

    private bool ShouldEncodePreview() {
        lock (_gate) {
            ThrowIfTerminalPublished();
            return !_previewSuppressed;
        }
    }

    private bool PublishPreview(
        GalateaSseFrame frame,
        string? phase = null
    ) {
        if (frame.IsTerminal) {
            throw new ArgumentException(
                "Preview publication requires a nonterminal frame.",
                nameof(frame)
            );
        }
        List<Channel<GalateaSseFrame>> disconnected = [];
        lock (_gate) {
            ThrowIfTerminalPublished();
            if (_previewSuppressed) {
                return false;
            }
            if (_previewEventCount
                    == GalateaSseLimits.MaximumPreviewEventCount
                || frame.Utf8Length
                    > GalateaSseLimits.MaximumPreviewUtf8Bytes
                        - _previewUtf8Bytes) {
                _previewSuppressed = true;
                return false;
            }
            _previewEventCount++;
            _previewUtf8Bytes += frame.Utf8Length;
            if (phase is not null) {
                _phase = phase;
            }
            _replayFrames.Add(frame);
            WriteSubscribersLocked(frame, disconnected);
        }
        CompleteDisconnected(disconnected);
        return true;
    }

    private void PublishTerminal(
        GalateaSseFrame frame,
        string status
    ) {
        if (!frame.IsTerminal) {
            throw new ArgumentException(
                "Terminal publication requires a terminal frame.",
                nameof(frame)
            );
        }
        List<Channel<GalateaSseFrame>> subscribers = [];
        lock (_gate) {
            ThrowIfTerminalPublished();
            if (frame.Utf8Length
                > GalateaSseLimits.MaximumTerminalFrameUtf8Bytes) {
                throw new InvalidOperationException(
                    "Galatea terminal SSE frame exceeded its reserved bound."
                );
            }
            if (_previewEventCount + 1
                    > GalateaSseLimits.MaximumReplayEventCount
                || frame.Utf8Length
                    > GalateaSseLimits.MaximumWholeReplayUtf8Bytes
                        - _previewUtf8Bytes) {
                throw new InvalidOperationException(
                    "Galatea terminal SSE frame exceeded the whole replay bound."
                );
            }
            _terminalPublished = true;
            _status = status;
            _replayFrames.Add(frame);
            WriteSubscribersLocked(frame, subscribers);
            subscribers.AddRange(_subscribers.Values);
            _subscribers.Clear();
        }
        StopController.Complete();
        CompleteDisconnected(subscribers);
    }

    private void WriteSubscribersLocked(
        GalateaSseFrame frame,
        List<Channel<GalateaSseFrame>> disconnected
    ) {
        foreach ((long subscriberId, Channel<GalateaSseFrame> subscriber)
                 in _subscribers.ToArray()) {
            if (subscriber.Writer.TryWrite(frame)) {
                continue;
            }
            _subscribers.Remove(subscriberId);
            disconnected.Add(subscriber);
        }
    }

    private void ThrowIfTerminalPublished() {
        if (_terminalPublished) {
            throw new InvalidOperationException(
                "Galatea SSE terminal frame was already published."
            );
        }
    }

    private static void CompleteDisconnected(
        IEnumerable<Channel<GalateaSseFrame>> subscribers
    ) {
        foreach (Channel<GalateaSseFrame> subscriber in subscribers) {
            subscriber.Writer.TryComplete();
        }
    }
}

internal sealed class GalateaTurnSubscription : IDisposable {
    private readonly GalateaLiveTurn _owner;
    private readonly long _subscriberId;
    private bool _disposed;

    public GalateaTurnSubscription(
        GalateaLiveTurn owner,
        long subscriberId,
        IReadOnlyList<GalateaSseFrame> replayFrames,
        ChannelReader<GalateaSseFrame> reader
    ) {
        _owner = owner;
        _subscriberId = subscriberId;
        ReplayFrames = replayFrames;
        Reader = reader;
    }

    public IReadOnlyList<GalateaSseFrame> ReplayFrames { get; }

    public ChannelReader<GalateaSseFrame> Reader { get; }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        _owner.Unsubscribe(_subscriberId);
    }
}

internal sealed class GalateaTurnException : Exception {
    public GalateaTurnException(string message, string? failureReason = null)
        : base(message) {
        FailureReason = failureReason;
    }

    public string? FailureReason { get; }
}
