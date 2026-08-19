using System.Collections.Concurrent;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Holds the Fiscalisation platform's bearer tokens between calls, one per credential.
/// </summary>
/// <remarks>
/// A singleton rather than state on the client, because the typed client is transient — the HTTP factory
/// builds a new one per resolution, and a token cached on it would be thrown away with it. Every sign-in
/// is also an audited "Login" event on the platform, so a client that re-authenticated per call would fill
/// the taxpayer's audit trail with this integration's own noise.
///
/// <para>Keyed by credential rather than holding one token, because the platform authorises each
/// fiscal-day call against the device named in the request and reads a single <c>FdmsDeviceId</c> claim to
/// do it: a fleet needs one account per device, and one shared slot would hand device B the token issued
/// for device A and collect a 403.</para>
///
/// The refresh is serialised per credential: a burst of callers arriving on one expired token issues one
/// sign-in, not one per caller, and a second credential refreshing does not wait behind the first.
/// </remarks>
public sealed class FiscalDayServiceAccountTokenStore
{
    private readonly ConcurrentDictionary<string, CredentialEntry> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The current token for one credential, signing in first if there is none, if the cached one is
    /// inside <paramref name="refreshSkew"/> of expiring, or if the caller was just refused with a 401.
    /// </summary>
    /// <param name="credentialKey">
    /// Which credential this is. Two devices configured with the same account share its token.
    /// </param>
    /// <param name="issue">
    /// Performs the sign-in. Called under this credential's gate, so it runs at most once per refresh
    /// however many callers are waiting.
    /// </param>
    /// <param name="refreshSkew">
    /// How much of a token's remaining life is treated as already spent, so a call is never made with a
    /// token that expires while it is in flight.
    /// </param>
    /// <param name="forceRefresh">
    /// Discards the cached token before deciding. Set after a 401: the platform can revoke a token before
    /// its stated expiry — a password change or a session revocation does exactly that — and the cached
    /// expiry says nothing about it.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait for the gate and the sign-in itself.</param>
    public async Task<string> GetAsync(
        string credentialKey,
        Func<CancellationToken, Task<JwtTokenApiResponse>> issue,
        TimeSpan refreshSkew,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);

        var entry = _entries.GetOrAdd(credentialKey ?? string.Empty, _ => new CredentialEntry());

        if (!forceRefresh && TryReadUsable(entry, refreshSkew, out var cached))
        {
            return cached;
        }

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while this one waited. Re-check before spending a sign-in,
            // but never when this caller is here because its own token was refused: that token is the one
            // sitting in the cache, and accepting it again would loop.
            if (!forceRefresh && TryReadUsable(entry, refreshSkew, out cached))
            {
                return cached;
            }

            var issued = await issue(cancellationToken);

            if (string.IsNullOrWhiteSpace(issued.AccessToken))
            {
                throw new FiscalisationApiException(
                    System.Net.HttpStatusCode.Unauthorized,
                    "ServiceAccountTokenEmpty",
                    "The Fiscalisation platform accepted the service account but returned no access token.");
            }

            var token = new CachedToken(issued.AccessToken.Trim(), NormalizeToUtc(issued.ExpiresAt));
            entry.Token = token;
            return token.AccessToken;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Drops one credential's cached token so its next call signs in again.</summary>
    public void Invalidate(string credentialKey)
    {
        if (_entries.TryGetValue(credentialKey ?? string.Empty, out var entry))
        {
            entry.Token = null;
        }
    }

    private static bool TryReadUsable(CredentialEntry entry, TimeSpan refreshSkew, out string token)
    {
        // One read of one reference, so the token and the expiry that was issued with it are always the
        // pair the platform sent. Read separately they can tear: a refresh landing between the two fields
        // hands out the new token judged against the old expiry, or the old token judged against the new.
        var cached = entry.Token;
        token = cached?.AccessToken ?? string.Empty;

        return cached is not null
               && !string.IsNullOrWhiteSpace(cached.AccessToken)
               && cached.ExpiresAtUtc - DateTime.UtcNow > refreshSkew;
    }

    /// <summary>
    /// Reads the platform's stated expiry as an instant.
    /// </summary>
    /// <remarks>
    /// The platform builds it from <c>DateTime.UtcNow.Add(lifetime)</c> but serialises it without a zone,
    /// so it arrives as <see cref="DateTimeKind.Unspecified"/> and would be read as this machine's local
    /// time. On a CAT host that is two hours of a token's life spent believing it had already gone, or —
    /// west of Greenwich — two hours of using one that had.
    /// </remarks>
    private static DateTime NormalizeToUtc(DateTime expiresAt) => expiresAt.Kind switch
    {
        DateTimeKind.Utc => expiresAt,
        DateTimeKind.Local => expiresAt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)
    };

    /// <summary>A token and the expiry it was issued with, replaced as one value.</summary>
    private sealed record CachedToken(string AccessToken, DateTime ExpiresAtUtc);

    private sealed class CredentialEntry
    {
        private CachedToken? _token;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>
        /// Published and read through <see cref="Volatile"/> because the singleton is shared by every
        /// request thread and nothing outside the sign-in itself takes the gate.
        /// </summary>
        public CachedToken? Token
        {
            get => Volatile.Read(ref _token);
            set => Volatile.Write(ref _token, value);
        }
    }
}
