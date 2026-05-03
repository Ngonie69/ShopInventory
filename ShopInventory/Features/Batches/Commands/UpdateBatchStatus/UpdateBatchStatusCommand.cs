using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Batches.Commands.UpdateBatchStatus;

public sealed record UpdateBatchStatusCommand(int BatchEntryId, string Status) : IRequest<ErrorOr<Success>>;