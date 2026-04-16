using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Sync.Queries.GetQueueStatus;

public sealed record GetQueueStatusQuery() : IRequest<ErrorOr<OfflineQueueStatusDto>>;
