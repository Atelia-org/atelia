using System.Collections.Concurrent;
using System.Threading.Channels;
using Atelia.Completion.Abstractions;

namespace Atelia.FamilyChat.Server;

internal sealed class FamilyChatLiveTurn {
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, Channel<StreamEventDto>> _subscribers = new();
    private readonly List<StreamEventDto> _replayEvents = new();
    private long _nextSubscriberId;
    private bool _streamCompleted;
    private CompletionStreamObserver? _observer;
    private bool _stopRequested;

    public FamilyChatLiveTurn(string userMessage, FamilyChatTurnOptions options) {
        TurnId = Guid.NewGuid().ToString("N");
        UserMessage = userMessage;
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Status = "running";
    }

    public string TurnId { get; }

    public string UserMessage { get; }

    public FamilyChatTurnOptions Options { get; }

    public string Status { get; private set; }

    public string? Phase { get; private set; }

    public Task? RunTask { get; set; }

    public bool StopRequested {
        get {
            lock (_gate) {
                return _stopRequested;
            }
        }
    }

    public FamilyChatTurnSubscription Subscribe() {
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

            return new FamilyChatTurnSubscription(this, subscriberId, _replayEvents.ToArray(), channel.Reader);
        }
    }

    public void Publish(StreamEventDto streamEvent, string? phase = null, string? status = null) {
        Channel<StreamEventDto>[] subscribers;
        bool completeSubscribers = false;

        lock (_gate) {
            _replayEvents.Add(streamEvent);
            if (phase is not null) {
                Phase = phase;
            }

            if (status is not null) {
                Status = status;
                completeSubscribers = status != "running";
                if (completeSubscribers) {
                    _streamCompleted = true;
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

    public void AttachObserver(CompletionStreamObserver observer) {
        ArgumentNullException.ThrowIfNull(observer);

        bool shouldStop;
        lock (_gate) {
            _observer = observer;
            shouldStop = _stopRequested;
        }

        if (shouldStop) {
            observer.ShouldStop = true;
        }
    }

    public bool RequestStop() {
        CompletionStreamObserver? observer;

        lock (_gate) {
            if (_streamCompleted) { return false; }

            _stopRequested = true;
            observer = _observer;
        }

        if (observer is not null) {
            observer.ShouldStop = true;
        }

        return true;
    }
}

internal sealed class FamilyChatTurnSubscription : IDisposable {
    private readonly FamilyChatLiveTurn _owner;
    private readonly long _subscriberId;
    private bool _disposed;

    public FamilyChatTurnSubscription(
        FamilyChatLiveTurn owner,
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

internal sealed class FamilyChatTurnException : Exception {
    public FamilyChatTurnException(string message, string? failureReason = null)
        : base(message) {
        FailureReason = failureReason;
    }

    public string? FailureReason { get; }
}
