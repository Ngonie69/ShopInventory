using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Queries.GetPasskeys;

public sealed record GetPasskeysQuery(Guid UserId) : IRequest<ErrorOr<List<PasskeyCredentialDto>>>;