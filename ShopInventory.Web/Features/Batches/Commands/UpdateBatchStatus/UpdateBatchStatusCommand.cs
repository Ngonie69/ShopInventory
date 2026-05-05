using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Batches.Commands.UpdateBatchStatus;

public sealed record UpdateBatchStatusCommand(
    int BatchEntryId,
    string CurrentStatus,
    string Status,
    string BatchNumber,
    string ItemCode,
    string WarehouseCode
) : IRequest<ErrorOr<Success>>;