using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Sync.Queries.GetConnectionLogs;

public sealed record GetConnectionLogsQuery(
    int Count = 50
) : IRequest<ErrorOr<List<ConnectionLogDto>>>;
