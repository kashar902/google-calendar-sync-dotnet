using Google;
using System.Net;

namespace GoogleCalendarSync.Services;

/// <summary>
/// Executes Google API calls with exponential backoff for rate-limit (403 rateLimitExceeded /
/// 429) and transient (5xx) errors. Non-transient errors are rethrown immediately.
/// </summary>
public static class RetryHelper
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        int maxAttempts = 5,
        CancellationToken ct = default)
    {
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (GoogleApiException ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                Console.WriteLine(
                    $"  [retry] transient error ({(int)ex.HttpStatusCode} {ex.Error?.Message}); " +
                    $"attempt {attempt}/{maxAttempts}, waiting {delay.TotalSeconds:0.#}s");
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    private static bool IsTransient(GoogleApiException ex)
    {
        if (ex.HttpStatusCode == HttpStatusCode.TooManyRequests) // 429
            return true;

        if ((int)ex.HttpStatusCode >= 500) // 5xx
            return true;

        // 403 with a rate-limit / user-rate-limit reason is retryable; other 403s are not.
        if (ex.HttpStatusCode == HttpStatusCode.Forbidden && ex.Error?.Errors is { } errors)
        {
            foreach (var e in errors)
            {
                if (e.Reason is "rateLimitExceeded" or "userRateLimitExceeded")
                    return true;
            }
        }
        return false;
    }
}
