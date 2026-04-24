using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Commands.CompletePasskeyRegistration;

public sealed record CompletePasskeyRegistrationCommand(
    Guid UserId,
    string SessionToken,
    string CredentialJson,
    string Origin,
    string RpId) : IRequest<ErrorOr<PasskeyCredentialDto>>;