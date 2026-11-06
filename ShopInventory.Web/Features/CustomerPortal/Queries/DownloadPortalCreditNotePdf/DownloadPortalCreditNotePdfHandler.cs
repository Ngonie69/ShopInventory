using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CustomerPortal.Queries.DownloadPortalCreditNotePdf;

/// <summary>
/// Fetches a credit note's PDF once the account it is on is known to be this login's.
/// </summary>
/// <remarks>
/// Two locks, one each side. Here, the account must be one the login reaches; the API then refuses the
/// note unless it is on that account. Neither trusts the page, which only offers notes it was given.
/// </remarks>
public sealed class DownloadPortalCreditNotePdfHandler(
    ICustomerLinkedAccountService linkedAccounts,
    ICreditNoteService creditNotes,
    ILogger<DownloadPortalCreditNotePdfHandler> logger
) : IRequestHandler<DownloadPortalCreditNotePdfQuery, ErrorOr<byte[]>>
{
    public async Task<ErrorOr<byte[]>> Handle(
        DownloadPortalCreditNotePdfQuery request,
        CancellationToken cancellationToken)
    {
        var cardCodes = await linkedAccounts.GetAllCardCodesAsync(request.PortalCardCode);
        if (!cardCodes.Contains(request.CardCode.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Refused credit note {DocEntry} on {CardCode} to portal login {PortalCardCode}",
                request.DocEntry, request.CardCode, request.PortalCardCode);
            return Errors.CreditNote.NotYourAccount;
        }

        var pdf = await creditNotes.GetCreditNotePdfAsync(request.DocEntry, request.CardCode.Trim(), cancellationToken);
        if (pdf is null || pdf.Length == 0)
        {
            return Errors.CreditNote.PdfFailed("We could not produce that credit note's PDF just now. Please try again.");
        }

        return pdf;
    }
}
