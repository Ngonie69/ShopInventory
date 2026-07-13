using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Idempotency;

public sealed class IdempotencyRequestStore(
    ApplicationDbContext context,
    IOptions<SecuritySettings> securitySettings) : IIdempotencyRequestStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly int _expirationMinutes = securitySettings.Value.IdempotencyKeyExpirationMinutes > 0
        ? securitySettings.Value.IdempotencyKeyExpirationMinutes
        : 60;

    public async Task<IdempotencyAcquireResult<TResponse>> TryAcquireAsync<TResponse>(
        string scope,
        string key,
        object request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("Idempotency scope is required.", nameof(scope));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Idempotency key is required.", nameof(key));
        }

        var normalizedScope = scope.Trim();
        var normalizedKey = key.Trim();
        var requestHash = ComputeHash(JsonSerializer.Serialize(request, SerializerOptions));
        var now = DateTime.UtcNow;

        var existing = await context.IdempotencyRequests
            .AsTracking()
            .FirstOrDefaultAsync(
                item => item.Scope == normalizedScope && item.IdempotencyKey == normalizedKey,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.ExpiresAtUtc <= now)
            {
                context.IdempotencyRequests.Remove(existing);
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                return BuildResult<TResponse>(existing, requestHash);
            }
        }

        var entity = new IdempotencyRequestEntity
        {
            Scope = normalizedScope,
            IdempotencyKey = normalizedKey,
            RequestHash = requestHash,
            Status = IdempotencyRequestStatus.InProgress,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(_expirationMinutes)
        };

        context.IdempotencyRequests.Add(entity);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new IdempotencyAcquireResult<TResponse>(
                IdempotencyAcquireOutcome.Acquired,
                entity.Id,
                CreatedAtUtc: entity.CreatedAtUtc,
                ExpiresAtUtc: entity.ExpiresAtUtc);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();

            var concurrent = await context.IdempotencyRequests
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.Scope == normalizedScope && item.IdempotencyKey == normalizedKey,
                    cancellationToken);

            if (concurrent is not null)
            {
                return BuildResult<TResponse>(concurrent, requestHash);
            }

            throw;
        }
    }

    public async Task CompleteAsync<TResponse>(
        long requestId,
        TResponse response,
        CancellationToken cancellationToken)
    {
        var entity = await context.IdempotencyRequests
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == requestId, cancellationToken);

        if (entity is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        entity.Status = IdempotencyRequestStatus.Completed;
        entity.CompletedAtUtc = now;
        entity.ExpiresAtUtc = now.AddMinutes(_expirationMinutes);
        entity.ResponsePayload = JsonSerializer.Serialize(response, SerializerOptions);

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ReleaseAsync(
        long requestId,
        CancellationToken cancellationToken)
    {
        var entity = await context.IdempotencyRequests
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == requestId, cancellationToken);

        if (entity is null || entity.Status == IdempotencyRequestStatus.Completed)
        {
            return;
        }

        context.IdempotencyRequests.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    private static IdempotencyAcquireResult<TResponse> BuildResult<TResponse>(
        IdempotencyRequestEntity entity,
        string requestHash)
    {
        if (!string.Equals(entity.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return new IdempotencyAcquireResult<TResponse>(
                IdempotencyAcquireOutcome.RequestMismatch,
                entity.Id,
                CreatedAtUtc: entity.CreatedAtUtc,
                ExpiresAtUtc: entity.ExpiresAtUtc);
        }

        if (entity.Status == IdempotencyRequestStatus.Completed && !string.IsNullOrWhiteSpace(entity.ResponsePayload))
        {
            var response = JsonSerializer.Deserialize<TResponse>(entity.ResponsePayload, SerializerOptions);
            if (response is not null)
            {
                return new IdempotencyAcquireResult<TResponse>(
                    IdempotencyAcquireOutcome.ReplayAvailable,
                    entity.Id,
                    response,
                    entity.CreatedAtUtc,
                    entity.ExpiresAtUtc);
            }
        }

        return new IdempotencyAcquireResult<TResponse>(
            IdempotencyAcquireOutcome.InProgress,
            entity.Id,
            CreatedAtUtc: entity.CreatedAtUtc,
            ExpiresAtUtc: entity.ExpiresAtUtc);
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
