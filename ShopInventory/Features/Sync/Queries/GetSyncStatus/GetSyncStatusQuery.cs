using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Sync.Queries.GetSyncStatus;

public sealed record GetSyncStatusQuery() : IRequest<ErrorOr<SyncStatusDashboardDto>>;
