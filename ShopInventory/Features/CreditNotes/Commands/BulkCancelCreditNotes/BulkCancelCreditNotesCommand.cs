using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CreditNotes.Commands.BulkCancelCreditNotes;

public sealed record BulkCancelCreditNotesCommand(
    BulkCancelCreditNotesRequest Request,
    Guid UserId) : IRequest<ErrorOr<BulkCancelCreditNotesResult>>;