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
    /// Reads the associated userId for a valid challenge token without consuming it.
    /// Returns null when the token is invalid or expired.
    /// </summary>
    Guid? GetChallengeUserId(string token);

    /// <summary>
    /// Consumes a valid challenge token after successful second-factor verification.
    /// </summary>
    void ConsumeChallenge(string token);
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

    public Guid? GetChallengeUserId(string token)
    {
        if (!_store.TryGetValue(token, out var challenge))
            return null;

        if (challenge.ExpiresAt < DateTime.UtcNow)
        {
            _store.TryRemove(token, out _);
            return null;
        }

        return challenge.UserId;
    }

    public void ConsumeChallenge(string token)
    {
        _store.TryRemove(token, out _);
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
