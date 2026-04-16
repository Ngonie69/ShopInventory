using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Sync.Queries.CheckSapConnection;

public sealed record CheckSapConnectionQuery() : IRequest<ErrorOr<SapConnectionStatusDto>>;
