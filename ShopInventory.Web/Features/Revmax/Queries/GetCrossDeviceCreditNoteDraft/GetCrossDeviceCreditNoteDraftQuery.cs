using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Revmax.Queries.GetCrossDeviceCreditNoteDraft;

public sealed record GetCrossDeviceCreditNoteDraftQuery(
    string CreditNoteNumber,
    string? OriginalInvoiceNumberOverride = null) : IRequest<ErrorOr<GetCrossDeviceCreditNoteDraftResult>>;