using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Batches.Commands.UpdateBatchStatus;

public sealed record UpdateBatchStatusCommand(
    int BatchEntryId,
    string Status,
    string BatchNumber,
    string ItemCode
) : IRequest<ErrorOr<Success>>;