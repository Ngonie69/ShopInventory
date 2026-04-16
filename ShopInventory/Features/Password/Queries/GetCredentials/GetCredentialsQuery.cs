using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Password.Queries.GetCredentials;

public sealed record GetCredentialsQuery(
    Guid UserId
) : IRequest<ErrorOr<UpdateCredentialsResponse>>;
