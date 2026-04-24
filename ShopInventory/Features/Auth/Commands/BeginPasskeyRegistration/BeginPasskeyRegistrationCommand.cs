using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Commands.BeginPasskeyRegistration;

public sealed record BeginPasskeyRegistrationCommand(
    Guid UserId,
    string FriendlyName,
    string Origin,
    string RpId) : IRequest<ErrorOr<PasskeyRegistrationOptionsResponse>>;