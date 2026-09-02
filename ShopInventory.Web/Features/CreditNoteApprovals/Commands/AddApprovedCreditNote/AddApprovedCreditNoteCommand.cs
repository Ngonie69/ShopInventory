using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CreditNoteApprovals.Commands.AddApprovedCreditNote;

public sealed record AddApprovedCreditNoteCommand(int Code, string ClientRequestId)
    : IRequest<ErrorOr<AddApprovedCreditNoteResultDto>>;
