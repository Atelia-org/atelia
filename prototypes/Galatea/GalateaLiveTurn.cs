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
    private bool _transportAborted;
    private string _status = "running";
    private string? _phase;

    public GalateaLiveTurn(
        string userMessage,
        GalateaTurnOptions options
    ) : this(
        new GalateaFreshInput.PlayerAction(userMessage),
        options
    ) { }

    public GalateaLiveTurn(
        GalateaFreshInput? freshInput,
        GalateaTurnOptions options,
        GalateaDurableReplyLease? durableReplyLease = null
    ) {
        TurnId = Guid.NewGuid().ToString("N");
        FreshInput = freshInput;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        DurableReplyLease = durableReplyLease;
        StopController = new GalateaTurnStopController();
    }

    public string TurnId { get; }

    internal GalateaFreshInput? FreshInput { get; }

    internal GalateaDurableReplyLease? DurableReplyLease { get; }

    public string? UserMessage => FreshInput?.DisplayText;

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

    internal bool TransportAborted {
        get {
            lock (_gate) {
                return _transportAborted;
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
            if (!_terminalPublished && !_transportAborted) {
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
    ) => PublishPreview(
            maximumBytes => GalateaSseFrames.TryStatus(
                code,
                changed,
                maximumBytes
            ),
            GalateaSseFrames.StatusCode(code)
        );

    internal void PublishReasoningDelta(string delta) =>
        PublishPreview(maximumBytes =>
            GalateaSseFrames.TryReasoningDelta(
                delta,
                maximumBytes
            ));

    internal void PublishTextDelta(string delta) =>
        PublishPreview(maximumBytes =>
            GalateaSseFrames.TryTextDelta(delta, maximumBytes));

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
            if (!_terminalPublished && !_transportAborted) {
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

    internal void AbortTransportWithoutTerminal() {
        List<Channel<GalateaSseFrame>> subscribers;
        lock (_gate) {
            if (_terminalPublished || _transportAborted) {
                return;
            }
            _transportAborted = true;
            _status = "failed";
            subscribers = [.. _subscribers.Values];
            _subscribers.Clear();
        }
        StopController.Complete();
        CompleteDisconnected(subscribers);
    }

    private bool PublishPreview(
        Func<int, GalateaSseFrame?> encode,
        string? phase = null
    ) {
        int maximumFrameBytes;
        lock (_gate) {
            ThrowIfPublicationClosed();
            if (_previewSuppressed) {
                return false;
            }
            if (_previewEventCount
                == GalateaSseLimits.MaximumPreviewEventCount) {
                _previewSuppressed = true;
                return false;
            }
            maximumFrameBytes =
                GalateaSseLimits.MaximumPreviewUtf8Bytes
                - _previewUtf8Bytes;
        }
        GalateaSseFrame? frame = encode(maximumFrameBytes);
        List<Channel<GalateaSseFrame>> disconnected = [];
        lock (_gate) {
            ThrowIfPublicationClosed();
            if (_previewSuppressed) {
                return false;
            }
            if (_previewEventCount
                    == GalateaSseLimits.MaximumPreviewEventCount
                || frame is null
                || frame.Utf8Length
                    > GalateaSseLimits.MaximumPreviewUtf8Bytes
                        - _previewUtf8Bytes) {
                _previewSuppressed = true;
                return false;
            }
            if (frame.IsTerminal) {
                throw new InvalidOperationException(
                    "Preview encoder produced a terminal frame."
                );
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
            ThrowIfPublicationClosed();
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

    private void ThrowIfPublicationClosed() {
        if (_terminalPublished) {
            throw new InvalidOperationException(
                "Galatea SSE terminal frame was already published."
            );
        }
        if (_transportAborted) {
            throw new InvalidOperationException(
                "Galatea SSE transport was already aborted."
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
    public GalateaTurnException(
        string message,
        string? failureReason = null,
        Exception? innerException = null
    ) : base(message, innerException) {
        FailureReason = failureReason;
    }

    public string? FailureReason { get; }
}
