using System.Collections.Concurrent;
using System.Threading.Channels;
using Atelia.Completion.Abstractions;

namespace Atelia.Galatea.Server;

internal sealed class GalateaLiveTurn {
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, Channel<StreamEventDto>> _subscribers = new();
    private readonly List<StreamEventDto> _replayEvents = new();
    private long _nextSubscriberId;
    private bool _streamCompleted;
    private string _status;
    private string? _phase;

    public GalateaLiveTurn(string userMessage, GalateaTurnOptions options) {
        TurnId = Guid.NewGuid().ToString("N");
        UserMessage = userMessage;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        StopController = new GalateaTurnStopController();
        _status = "running";
    }

    public string TurnId { get; }

    public string UserMessage { get; }

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

    public Task? RunTask { get; set; }

    internal GalateaTurnStopController StopController { get; }

    public CompletionStreamObserver Observer => StopController.Observer;

    public CancellationToken PreDispatchStopToken =>
        StopController.PreDispatchStopToken;

    public bool StopRequested => StopController.StopRequested;

    public GalateaTurnSubscription Subscribe() {
        lock (_gate) {
            long subscriberId = Interlocked.Increment(ref _nextSubscriberId);
            var channel = Channel.CreateUnbounded<StreamEventDto>(
                new UnboundedChannelOptions {
                    SingleReader = true,
                    SingleWriter = false,
                }
            );
            if (!_streamCompleted) {
                _subscribers[subscriberId] = channel;
            }
            else {
                channel.Writer.TryComplete();
            }

            return new GalateaTurnSubscription(this, subscriberId, _replayEvents.ToArray(), channel.Reader);
        }
    }

    public void Publish(StreamEventDto streamEvent, string? phase = null, string? status = null) {
        Channel<StreamEventDto>[] subscribers;
        bool completeSubscribers = false;

        lock (_gate) {
            _replayEvents.Add(streamEvent);
            if (phase is not null) {
                _phase = phase;
            }

            if (status is not null) {
                _status = status;
                completeSubscribers = status != "running";
                if (completeSubscribers) {
                    _streamCompleted = true;
                    StopController.Complete();
                }
            }

            subscribers = _subscribers.Values.ToArray();
            if (completeSubscribers) {
                _subscribers.Clear();
            }
        }

        foreach (var subscriber in subscribers) {
            subscriber.Writer.TryWrite(streamEvent);
        }

        if (!completeSubscribers) { return; }

        foreach (var subscriber in subscribers) {
            subscriber.Writer.TryComplete();
        }
    }

    public void Complete() {
        Channel<StreamEventDto>[] subscribers;

        StopController.Complete();

        lock (_gate) {
            if (_streamCompleted) { return; }

            _streamCompleted = true;
            subscribers = _subscribers.Values.ToArray();
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers) {
            subscriber.Writer.TryComplete();
        }
    }

    public void Unsubscribe(long subscriberId) {
        if (_subscribers.TryRemove(subscriberId, out var subscriber)) {
            subscriber.Writer.TryComplete();
        }
    }

    public bool RequestStop() => StopController.RequestStop();
}

internal sealed class GalateaTurnSubscription : IDisposable {
    private readonly GalateaLiveTurn _owner;
    private readonly long _subscriberId;
    private bool _disposed;

    public GalateaTurnSubscription(
        GalateaLiveTurn owner,
        long subscriberId,
        IReadOnlyList<StreamEventDto> replayEvents,
        ChannelReader<StreamEventDto> reader
    ) {
        _owner = owner;
        _subscriberId = subscriberId;
        ReplayEvents = replayEvents;
        Reader = reader;
    }

    public IReadOnlyList<StreamEventDto> ReplayEvents { get; }

    public ChannelReader<StreamEventDto> Reader { get; }

    public void Dispose() {
        if (_disposed) { return; }

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
