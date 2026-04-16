using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Password.Commands.UpdateCredentials;

public sealed record UpdateCredentialsCommand(
    Guid UserId,
    UpdateCredentialsRequest Request
) : IRequest<ErrorOr<UpdateCredentialsResponse>>;
