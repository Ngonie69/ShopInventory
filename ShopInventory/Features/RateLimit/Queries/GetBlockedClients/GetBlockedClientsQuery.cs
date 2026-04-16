using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.RateLimit.Queries.GetBlockedClients;

public sealed record GetBlockedClientsQuery() : IRequest<ErrorOr<List<ApiRateLimitDto>>>;
