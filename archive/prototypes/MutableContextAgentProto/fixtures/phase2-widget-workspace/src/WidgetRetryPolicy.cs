namespace WidgetFixture;

public sealed class WidgetRetryPolicy {
    public static WidgetRetryPolicy None { get; } = new() {
        RetryCount = 0,
        Delay = TimeSpan.Zero,
    };

    public int RetryCount { get; init; } = 2;

    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(200);

    public bool ShouldRetry(int completedAttempts) {
        return completedAttempts < RetryCount;
    }

    public ValueTask WaitBeforeRetryAsync(CancellationToken cancellationToken) {
        return Delay <= TimeSpan.Zero
            ? ValueTask.CompletedTask
            : new ValueTask(Task.Delay(Delay, cancellationToken));
    }
}
