namespace RustProxy.Core.Tools;

/// <summary>Writes progress messages for fetch operations.</summary>
public interface IFetchLogger
{
    /// <summary>Writes an informational message.</summary>
    void Info(string message);

    /// <summary>Writes a retry warning.</summary>
    void Retry(string operation, int attempt, int maxAttempts, Exception exception, TimeSpan delay);
}

/// <summary>Writes fetch progress to the console.</summary>
public sealed class ConsoleFetchLogger : IFetchLogger
{
    /// <inheritdoc />
    public void Info(string message) => Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");

    /// <inheritdoc />
    public void Retry(string operation, int attempt, int maxAttempts, Exception exception, TimeSpan delay) =>
        Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] retry {attempt}/{maxAttempts} for {operation} in {delay.TotalSeconds:0.#}s: {exception.Message}");
}

/// <summary>Retries transient asynchronous operations with exponential backoff.</summary>
public sealed class RetryExecutor(IFetchLogger logger)
{
    /// <summary>Runs an operation until it succeeds or attempts are exhausted.</summary>
    public async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> action, int maxAttempts, TimeSpan initialDelay, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        if (maxAttempts is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (initialDelay < TimeSpan.Zero || initialDelay > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(initialDelay));
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await action();
            }
            catch (Exception exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested && IsRetryable(exception))
            {
                last = exception;
                var delay = exception.Data["RetryAfter"] is TimeSpan retryAfter
                    ? TimeSpan.FromMilliseconds(Math.Clamp(retryAfter.TotalMilliseconds, 0, 300_000))
                    : TimeSpan.FromMilliseconds(Math.Min(300_000, initialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)) * (0.8 + Random.Shared.NextDouble() * 0.2));
                logger.Retry(operation, attempt, maxAttempts, exception, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw last ?? new InvalidOperationException($"Operation failed: {operation}");
    }

    private static bool IsRetryable(Exception exception) =>
        exception is IOException or TimeoutException or TaskCanceledException ||
        exception is HttpRequestException httpException &&
        (httpException.StatusCode is null || (int)httpException.StatusCode >= 500 || (int)httpException.StatusCode is 408 or 429);
}
