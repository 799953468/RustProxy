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
    public void Retry(string operation, int attempt, int maxAttempts, Exception exception, TimeSpan delay) => Console.Error.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] retry {attempt}/{maxAttempts} for {operation} in {delay.TotalSeconds:0.#}s: {exception.Message}");
}

/// <summary>Retries transient asynchronous operations with exponential backoff.</summary>
public sealed class RetryExecutor(IFetchLogger logger)
{
    /// <summary>Runs an operation until it succeeds or attempts are exhausted.</summary>
    public async Task<T> ExecuteAsync<T>(string operation, Func<Task<T>> action, int maxAttempts, TimeSpan initialDelay, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try { return await action(); }
            catch (Exception exception) when (attempt < maxAttempts && IsRetryable(exception))
            {
                last = exception;
                var delay = TimeSpan.FromMilliseconds(initialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                logger.Retry(operation, attempt, maxAttempts, exception, delay);
                await Task.Delay(delay, cancellationToken);
            }
        }
        throw last ?? new InvalidOperationException($"Operation failed: {operation}");
    }

    private static bool IsRetryable(Exception exception) => exception is IOException or TimeoutException or TaskCanceledException || exception is HttpRequestException httpException && (httpException.StatusCode is null || (int)httpException.StatusCode >= 500);
}
