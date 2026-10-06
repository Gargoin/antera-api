using Microsoft.Extensions.Caching.Memory;

namespace AnteraApp.Api.Services;

public sealed class LoginAttemptLimiter(IMemoryCache cache)
{
    private const int MaximumFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();

    public TimeSpan? GetRetryAfter(string email, string clientKey)
    {
        lock (_gate)
        {
            var attempts = GetAttempts(email, clientKey);
            RemoveExpiredAttempts(attempts);
            return attempts.Failures.Count < MaximumFailures
                ? null
                : attempts.Failures.Peek().Add(Window).Subtract(DateTime.UtcNow);
        }
    }

    public TimeSpan? RecordFailure(string email, string clientKey)
    {
        lock (_gate)
        {
            var attempts = GetAttempts(email, clientKey);
            RemoveExpiredAttempts(attempts);
            attempts.Failures.Enqueue(DateTime.UtcNow);
            return attempts.Failures.Count < MaximumFailures
                ? null
                : attempts.Failures.Peek().Add(Window).Subtract(DateTime.UtcNow);
        }
    }

    public void Clear(string email, string clientKey) => cache.Remove(GetCacheKey(email, clientKey));

    private LoginAttempts GetAttempts(string email, string clientKey) =>
        cache.GetOrCreate(GetCacheKey(email, clientKey), entry =>
        {
            entry.SetAbsoluteExpiration(Window);
            entry.SetSize(1);
            return new LoginAttempts();
        })!;

    private static void RemoveExpiredAttempts(LoginAttempts attempts)
    {
        var minimumTimestamp = DateTime.UtcNow.Subtract(Window);
        while (attempts.Failures.TryPeek(out var timestamp) && timestamp <= minimumTimestamp)
        {
            attempts.Failures.Dequeue();
        }
    }

    private static string GetCacheKey(string email, string clientKey) =>
        $"login-attempts:{email.Trim().ToUpperInvariant()}:{clientKey}";

    private sealed class LoginAttempts
    {
        public Queue<DateTime> Failures { get; } = new();
    }
}
