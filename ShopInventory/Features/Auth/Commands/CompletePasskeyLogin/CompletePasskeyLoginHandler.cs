using System.Text.Json;
using ErrorOr;
using Fido2NetLib;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Auth.Passkeys;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.CompletePasskeyLogin;

public sealed class CompletePasskeyLoginHandler(
    ApplicationDbContext dbContext,
    IPasskeyOperationStore operationStore,
    IAuthService authService,
    IAuditService auditService,
    ILogger<CompletePasskeyLoginHandler> logger
) : IRequestHandler<CompletePasskeyLoginCommand, ErrorOr<AuthLoginResponse>>
{
    public async Task<ErrorOr<AuthLoginResponse>> Handle(
        CompletePasskeyLoginCommand request,
        CancellationToken cancellationToken)
    {
        var operation = operationStore.ConsumeAssertion(request.SessionToken);
        if (operation is null ||
            !string.Equals(operation.Value.Origin, request.Origin.Trim(), StringComparison.Ordinal) ||
            !string.Equals(operation.Value.RpId, request.RpId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Invalid or expired passkey login operation");
            return Errors.Auth.InvalidPasskeyOperation;
        }

        var credentialId = ExtractCredentialId(request.CredentialJson);
        if (string.IsNullOrWhiteSpace(credentialId))
        {
            return Errors.Auth.PasskeyLoginFailed("Invalid passkey credential payload");
        }

        var storedCredential = await dbContext.PasskeyCredentials
            .Include(pc => pc.User)
            .FirstOrDefaultAsync(pc => pc.CredentialId == credentialId, cancellationToken);

        if (storedCredential?.User is null || !storedCredential.User.IsActive)
        {
            try { await auditService.LogAsync(AuditActions.PasskeyLoginFailed, "Unknown", "Unknown", "User", null, "Failed passkey login attempt", null, false, "Credential not found"); } catch { }
            return Errors.Auth.PasskeyLoginFailed("Passkey not recognized");
        }

        if (storedCredential.User.LockoutEnd.HasValue && storedCredential.User.LockoutEnd.Value > DateTime.UtcNow)
        {
            return Errors.Auth.LockedOut;
        }

        var fidoOrError = PasskeyRelyingParty.Create(request.Origin, request.RpId);
        if (fidoOrError.IsError)
        {
            return fidoOrError.Errors;
        }

        AuthenticatorAssertionRawResponse? assertionResponse;
        try
        {
            assertionResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(request.CredentialJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not deserialize passkey assertion response");
            return Errors.Auth.PasskeyLoginFailed("Invalid passkey credential payload");
        }

        if (assertionResponse is null)
        {
            return Errors.Auth.PasskeyLoginFailed("Invalid passkey credential payload");
        }

        try
        {
            var assertionResult = await fidoOrError.Value.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = assertionResponse,
                OriginalOptions = operation.Value.Options,
                StoredPublicKey = storedCredential.PublicKey,
                StoredSignatureCounter = storedCredential.SignatureCounter > uint.MaxValue ? uint.MaxValue : (uint)storedCredential.SignatureCounter,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle is not null && storedCredential.UserHandle.SequenceEqual(args.UserHandle))
            }, cancellationToken);

            storedCredential.SignatureCounter = assertionResult.SignCount;
            storedCredential.LastUsedAt = DateTime.UtcNow;
            storedCredential.UpdatedAt = DateTime.UtcNow;

            var loginResponse = await authService.CompletePasskeyLoginAsync(storedCredential.UserId, request.IpAddress, cancellationToken);
            if (loginResponse is null)
            {
                return Errors.Auth.PasskeyLoginFailed("Passkey login failed");
            }

            try { await auditService.LogAsync(AuditActions.PasskeyLogin, storedCredential.User.Username, storedCredential.User.Role, "User", storedCredential.UserId.ToString(), $"User {storedCredential.User.Username} logged in with a passkey"); } catch { }

            return loginResponse;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Passkey assertion verification failed for user {UserId}", storedCredential.UserId);
            try { await auditService.LogAsync(AuditActions.PasskeyLoginFailed, storedCredential.User.Username, storedCredential.User.Role, "User", storedCredential.UserId.ToString(), $"Failed passkey login for {storedCredential.User.Username}", null, false, ex.Message); } catch { }
            return Errors.Auth.PasskeyLoginFailed("Passkey verification failed");
        }
    }

    private static string? ExtractCredentialId(string credentialJson)
    {
        using var document = JsonDocument.Parse(credentialJson);
        return document.RootElement.TryGetProperty("id", out var idProperty)
            ? idProperty.GetString()
            : null;
    }
}