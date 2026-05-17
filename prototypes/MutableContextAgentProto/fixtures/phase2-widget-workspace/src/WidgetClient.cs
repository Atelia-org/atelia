namespace WidgetFixture;

public sealed class WidgetClient {
    private readonly WidgetOptions _options;
    private readonly WidgetRetryPolicy _retryPolicy;

    public WidgetClient(WidgetOptions options, WidgetRetryPolicy? retryPolicy = null) {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _retryPolicy = retryPolicy ?? WidgetRetryPolicy.None;
    }

    public async ValueTask<string> GetWidgetAsync(
        string widgetId,
        CancellationToken cancellationToken = default
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);

        for (var attempt = 0; ; attempt++) {
            using var timeout = new CancellationTokenSource(_options.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token
            );

            try {
                return await SendOnceAsync(widgetId, linked.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested
                    && timeout.IsCancellationRequested
                    && _retryPolicy.ShouldRetry(attempt)) {
                await _retryPolicy.WaitBeforeRetryAsync(cancellationToken);
            }
            catch (TimeoutException) when (_retryPolicy.ShouldRetry(attempt)) {
                await _retryPolicy.WaitBeforeRetryAsync(cancellationToken);
            }
        }
    }

    private async ValueTask<string> SendOnceAsync(
        string widgetId,
        CancellationToken cancellationToken
    ) {
        await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        return $"{_options.Endpoint}/widgets/{widgetId}";
    }
}
