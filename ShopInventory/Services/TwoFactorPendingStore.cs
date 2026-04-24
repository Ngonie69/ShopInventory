using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ShopInventory.Services;

public interface ITwoFactorPendingStore
{
    /// <summary>
    /// Creates a short-lived challenge token for the given user. Returns the opaque token.
    /// </summary>
    string CreateChallenge(Guid userId);

    /// <summary>
    /// Validates and atomically consumes the challenge token.
    /// Returns the associated userId if valid, otherwise returns null.
    /// </summary>
    Guid? ConsumeChallenge(string token);
}

public sealed class TwoFactorPendingStore : ITwoFactorPendingStore
{
    private static readonly TimeSpan ChallengeExpiry = TimeSpan.FromMinutes(5);

    private readonly record struct PendingChallenge(Guid UserId, DateTime ExpiresAt);

    // Token → PendingChallenge. Static so it survives DI scope changes.
    private static readonly ConcurrentDictionary<string, PendingChallenge> _store = new();

    public string CreateChallenge(Guid userId)
    {
        // Purge any stale entry for this user first (one active challenge at a time)
        foreach (var kvp in _store)
        {
            if (kvp.Value.UserId == userId)
                _store.TryRemove(kvp.Key, out _);
        }

        var token = GenerateToken();
        _store[token] = new PendingChallenge(userId, DateTime.UtcNow.Add(ChallengeExpiry));
        return token;
    }

    public Guid? ConsumeChallenge(string token)
    {
        if (!_store.TryRemove(token, out var challenge))
            return null;

        if (challenge.ExpiresAt < DateTime.UtcNow)
            return null;

        return challenge.UserId;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
