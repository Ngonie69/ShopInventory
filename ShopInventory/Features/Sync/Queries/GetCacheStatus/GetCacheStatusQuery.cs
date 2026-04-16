using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Sync.Queries.GetCacheStatus;

public sealed record GetCacheStatusQuery() : IRequest<ErrorOr<List<CacheSyncStatusDto>>>;
