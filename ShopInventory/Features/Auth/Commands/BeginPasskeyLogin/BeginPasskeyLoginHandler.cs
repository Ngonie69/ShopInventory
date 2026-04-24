using System.Text.Json;
using ErrorOr;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Fido2NetLib.Serialization;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.Auth.Passkeys;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Commands.BeginPasskeyLogin;

public sealed class BeginPasskeyLoginHandler(
    IPasskeyOperationStore operationStore,
    ILogger<BeginPasskeyLoginHandler> logger
) : IRequestHandler<BeginPasskeyLoginCommand, ErrorOr<PasskeyAssertionOptionsResponse>>
{
    public Task<ErrorOr<PasskeyAssertionOptionsResponse>> Handle(
        BeginPasskeyLoginCommand request,
        CancellationToken cancellationToken)
    {
        var fidoOrError = PasskeyRelyingParty.Create(request.Origin, request.RpId);
        if (fidoOrError.IsError)
        {
            return Task.FromResult<ErrorOr<PasskeyAssertionOptionsResponse>>(fidoOrError.Errors);
        }

        var options = fidoOrError.Value.GetAssertionOptions(new GetAssertionOptionsParams
        {
            UserVerification = UserVerificationRequirement.Required,
            AllowedCredentials = Array.Empty<PublicKeyCredentialDescriptor>()
        });

        var sessionToken = operationStore.StoreAssertion(request.Origin.Trim(), request.RpId.Trim(), options);

        logger.LogInformation("Started passkey login ceremony for RP ID {RpId}", request.RpId);

        return Task.FromResult<ErrorOr<PasskeyAssertionOptionsResponse>>(new PasskeyAssertionOptionsResponse
        {
            SessionToken = sessionToken,
            OptionsJson = JsonSerializer.Serialize(options, FidoModelSerializerContext.Default.AssertionOptions)
        });
    }
}