using Microsoft.Extensions.Caching.Memory;

namespace AnteraApp.Api.Services;

public sealed class LoginAttemptLimiter(IMemoryCache cache)
{
    private const int MaximumFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();

    public TimeSpan? GetRetryAfter(string email)
    {
        lock (_gate)
        {
            var attempts = GetAttempts(email);
            RemoveExpiredAttempts(attempts);
            return attempts.Failures.Count < MaximumFailures
                ? null
                : attempts.Failures.Peek().Add(Window).Subtract(DateTime.UtcNow);
        }
    }

    public TimeSpan? RecordFailure(string email)
    {
        lock (_gate)
        {
            var attempts = GetAttempts(email);
            RemoveExpiredAttempts(attempts);
            attempts.Failures.Enqueue(DateTime.UtcNow);
            return attempts.Failures.Count < MaximumFailures
                ? null
                : attempts.Failures.Peek().Add(Window).Subtract(DateTime.UtcNow);
        }
    }

    public void Clear(string email) => cache.Remove(GetCacheKey(email));

    private LoginAttempts GetAttempts(string email) =>
        cache.GetOrCreate(GetCacheKey(email), entry =>
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

    private static string GetCacheKey(string email) =>
        $"login-attempts:{email.Trim().ToUpperInvariant()}";

    private sealed class LoginAttempts
    {
        public Queue<DateTime> Failures { get; } = new();
    }
}
