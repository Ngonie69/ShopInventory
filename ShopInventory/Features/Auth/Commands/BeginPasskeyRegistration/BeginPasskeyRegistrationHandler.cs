using System.Text.Json;
using ErrorOr;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Auth.Passkeys;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.BeginPasskeyRegistration;

public sealed class BeginPasskeyRegistrationHandler(
    ApplicationDbContext dbContext,
    IPasskeyOperationStore operationStore,
    ILogger<BeginPasskeyRegistrationHandler> logger
) : IRequestHandler<BeginPasskeyRegistrationCommand, ErrorOr<PasskeyRegistrationOptionsResponse>>
{
    public async Task<ErrorOr<PasskeyRegistrationOptionsResponse>> Handle(
        BeginPasskeyRegistrationCommand request,
        CancellationToken cancellationToken)
    {
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

        var friendlyName = string.IsNullOrWhiteSpace(request.FriendlyName)
            ? "Passkey"
            : request.FriendlyName.Trim();

        var options = fidoOrError.Value.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = request.UserId.ToByteArray(),
                Name = user.Username,
                DisplayName = user.Username
            },
            ExcludeCredentials = Array.Empty<PublicKeyCredentialDescriptor>(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                RequireResidentKey = true,
                UserVerification = UserVerificationRequirement.Required
            },
            AttestationPreference = AttestationConveyancePreference.None,
            PubKeyCredParams = new[]
            {
                new PubKeyCredParam(COSE.Algorithm.ES256, PublicKeyCredentialType.PublicKey),
                new PubKeyCredParam(COSE.Algorithm.RS256, PublicKeyCredentialType.PublicKey)
            }
        });

        var sessionToken = operationStore.StoreRegistration(request.UserId, request.Origin.Trim(), request.RpId.Trim(), friendlyName, options);

        logger.LogInformation("Started passkey registration ceremony for user {UserId}", request.UserId);

        return new PasskeyRegistrationOptionsResponse
        {
            SessionToken = sessionToken,
            OptionsJson = JsonSerializer.Serialize(options, FidoModelSerializerContext.Default.CredentialCreateOptions)
        };
    }
}