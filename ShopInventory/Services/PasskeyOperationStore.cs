using System.Collections.Concurrent;
using System.Security.Cryptography;
using Fido2NetLib;

namespace ShopInventory.Services;

public interface IPasskeyOperationStore
{
    string StoreRegistration(Guid userId, string origin, string rpId, string friendlyName, CredentialCreateOptions options);

    (Guid UserId, string Origin, string RpId, string FriendlyName, CredentialCreateOptions Options)? ConsumeRegistration(string token);

    string StoreAssertion(string origin, string rpId, AssertionOptions options);

    (string Origin, string RpId, AssertionOptions Options)? ConsumeAssertion(string token);
}

public sealed class PasskeyOperationStore : IPasskeyOperationStore
{
    private static readonly TimeSpan OperationLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, StoredRegistrationOperation> _registrations = new();
    private readonly ConcurrentDictionary<string, StoredAssertionOperation> _assertions = new();

    public string StoreRegistration(Guid userId, string origin, string rpId, string friendlyName, CredentialCreateOptions options)
    {
        PurgeExpired();

        var token = GenerateToken();
        _registrations[token] = new StoredRegistrationOperation(
            userId,
            origin,
            rpId,
            friendlyName,
            options,
            DateTime.UtcNow.Add(OperationLifetime));

        return token;
    }

    public (Guid UserId, string Origin, string RpId, string FriendlyName, CredentialCreateOptions Options)? ConsumeRegistration(string token)
    {
        PurgeExpired();

        if (!_registrations.TryRemove(token, out var operation) || operation.ExpiresAt < DateTime.UtcNow)
        {
            return null;
        }

        return (operation.UserId, operation.Origin, operation.RpId, operation.FriendlyName, operation.Options);
    }

    public string StoreAssertion(string origin, string rpId, AssertionOptions options)
    {
        PurgeExpired();

        var token = GenerateToken();
        _assertions[token] = new StoredAssertionOperation(
            origin,
            rpId,
            options,
            DateTime.UtcNow.Add(OperationLifetime));

        return token;
    }

    public (string Origin, string RpId, AssertionOptions Options)? ConsumeAssertion(string token)
    {
        PurgeExpired();

        if (!_assertions.TryRemove(token, out var operation) || operation.ExpiresAt < DateTime.UtcNow)
        {
            return null;
        }

        return (operation.Origin, operation.RpId, operation.Options);
    }

    private void PurgeExpired()
    {
        var now = DateTime.UtcNow;

        foreach (var operation in _registrations.Where(x => x.Value.ExpiresAt < now).ToList())
        {
            _registrations.TryRemove(operation.Key, out _);
        }

        foreach (var operation in _assertions.Where(x => x.Value.ExpiresAt < now).ToList())
        {
            _assertions.TryRemove(operation.Key, out _);
        }
    }

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private sealed record StoredRegistrationOperation(
        Guid UserId,
        string Origin,
        string RpId,
        string FriendlyName,
        CredentialCreateOptions Options,
        DateTime ExpiresAt);

    private sealed record StoredAssertionOperation(
        string Origin,
        string RpId,
        AssertionOptions Options,
        DateTime ExpiresAt);
}