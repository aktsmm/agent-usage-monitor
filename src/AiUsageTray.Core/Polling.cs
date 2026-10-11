namespace AiUsageTray.Core;

public static class Polling
{
    public static TimeSpan NextDelay(PollingOptions options, int failures, TimeSpan? retryAfter)
    {
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(failures);
        var delay = TimeSpan.FromTicks((long)Math.Min(options.MaximumBackoff.Ticks,
            options.Interval.Ticks * Math.Pow(2, Math.Min(failures, 16))));
        return retryAfter is { } serverDelay && serverDelay > delay ? serverDelay : delay;
    }

    public static async Task RunAsync(IUsageProvider provider, UsageState state, StateStore store,
        Func<FetchResult, IReadOnlyList<NotificationCandidate>, Task> onResult,
        bool once, CancellationToken cancellationToken)
    {
        int failures = 0;
        do
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(state.Polling.Timeout);
            FetchResult result;
            try
            {
                result = await provider.FetchAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = FetchResult.Failed(new(FailureKind.Timeout, "poll_timeout", DateTimeOffset.UtcNow));
            }
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = UsageEngine.Apply(state, result, DateTimeOffset.UtcNow);
            await store.SaveAsync(state, cancellationToken);
            await onResult(result, candidates);
            failures = result.Succeeded ? 0 : failures + 1;
            if (once)
                return;
            var retryAfter = result.Failures.Select(f => f.RetryAfter ?? TimeSpan.Zero).DefaultIfEmpty().Max();
            await Task.Delay(NextDelay(state.Polling, failures, retryAfter), cancellationToken);
        } while (true);
    }
}
