using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Invoices.Commands.CancelInvoice;

/// <summary>
/// Withdraws a posted invoice in full by raising the credit note that reverses it, and tells the
/// till that issued the receipt.
/// </summary>
/// <param name="DocEntry">The SAP document entry of the invoice being cancelled.</param>
/// <param name="Reason">
/// One of the values SAP's <c>U_Reasons</c> field defines. It is written to every credit note line,
/// which is where SAP's own return reporting reads it from.
/// </param>
/// <param name="Comments">Anything the person cancelling wants to add, appended to the SAP header.</param>
/// <param name="UserId">Who is cancelling.</param>
/// <param name="ClientRequestId">
/// Idempotency key. A cancellation posts a real credit note to SAP, so a retried request must
/// replay rather than raise a second one.
/// </param>
public sealed record CancelInvoiceCommand(
    int DocEntry,
    string Reason,
    string? Comments,
    Guid UserId,
    string? ClientRequestId
) : IRequest<ErrorOr<CancelInvoiceResult>>;
