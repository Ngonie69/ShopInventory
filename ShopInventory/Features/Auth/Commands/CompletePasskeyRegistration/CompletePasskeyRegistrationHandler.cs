using System.Text.Json;
using ErrorOr;
using Fido2NetLib;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Auth.Passkeys;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.CompletePasskeyRegistration;

public sealed class CompletePasskeyRegistrationHandler(
    ApplicationDbContext dbContext,
    IPasskeyOperationStore operationStore,
    IAuditService auditService,
    ILogger<CompletePasskeyRegistrationHandler> logger
) : IRequestHandler<CompletePasskeyRegistrationCommand, ErrorOr<PasskeyCredentialDto>>
{
    public async Task<ErrorOr<PasskeyCredentialDto>> Handle(
        CompletePasskeyRegistrationCommand request,
        CancellationToken cancellationToken)
    {
        var operation = operationStore.ConsumeRegistration(request.SessionToken);
        if (operation is null ||
            operation.Value.UserId != request.UserId ||
            !string.Equals(operation.Value.Origin, request.Origin.Trim(), StringComparison.Ordinal) ||
            !string.Equals(operation.Value.RpId, request.RpId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Invalid or expired passkey registration operation for user {UserId}", request.UserId);
            return Errors.Auth.InvalidPasskeyOperation;
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId && u.IsActive, cancellationToken);

        if (user is null)
        {
            return Errors.Auth.UserNotFound;
        }

        var fidoOrError = PasskeyRelyingParty.Create(request.Origin, request.RpId);
        if (fidoOrError.IsError)
        {
            return fidoOrError.Errors;
        }

        AuthenticatorAttestationRawResponse? attestationResponse;
        try
        {
            attestationResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(request.CredentialJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not deserialize passkey attestation response for user {UserId}", request.UserId);
            return Errors.Auth.PasskeyRegistrationFailed("Invalid passkey registration payload");
        }

        if (attestationResponse is null)
        {
            return Errors.Auth.PasskeyRegistrationFailed("Invalid passkey registration payload");
        }

        try
        {
            var registrationResult = await fidoOrError.Value.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = attestationResponse,
                OriginalOptions = operation.Value.Options,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    !await dbContext.PasskeyCredentials.AsNoTracking()
                        .AnyAsync(pc => pc.CredentialId == Base64UrlEncoder.Encode(args.CredentialId), token)
            }, cancellationToken);

            var credential = new PasskeyCredential
            {
                Id = Guid.NewGuid(),
                UserId = request.UserId,
                CredentialId = Base64UrlEncoder.Encode(registrationResult.Id),
                PublicKey = registrationResult.PublicKey,
                UserHandle = request.UserId.ToByteArray(),
                SignatureCounter = registrationResult.SignCount,
                FriendlyName = operation.Value.FriendlyName,
                AuthenticatorTransports = registrationResult.Transports is null
                    ? null
                    : JsonSerializer.Serialize(registrationResult.Transports),
                CreatedAt = DateTime.UtcNow
            };

            dbContext.PasskeyCredentials.Add(credential);
            await dbContext.SaveChangesAsync(cancellationToken);

            try { await auditService.LogAsync(AuditActions.RegisterPasskey, user.Username, user.Role, "User", user.Id.ToString(), $"Registered passkey '{credential.FriendlyName}'"); } catch { }

            return new PasskeyCredentialDto
            {
                Id = credential.Id,
                FriendlyName = credential.FriendlyName,
                CreatedAt = credential.CreatedAt,
                LastUsedAt = credential.LastUsedAt
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Passkey registration failed for user {UserId}", request.UserId);
            return Errors.Auth.PasskeyRegistrationFailed("Passkey registration failed");
        }
    }
}