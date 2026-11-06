using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.CustomerPortal.Queries.GetPortalCreditNotes;

/// <summary>
/// Reads the credit notes of every account a portal login reaches — one, or a multi-account
/// customer's main and sub accounts — in one API call.
/// </summary>
/// <remarks>
/// The portal calls the API with the integration key alone, so the API cannot tell whose page this is:
/// the accounts come from this login's own record, and the answer is filtered to them again on the way
/// back rather than trusted.
/// </remarks>
public sealed class GetPortalCreditNotesHandler(
    ICustomerLinkedAccountService linkedAccounts,
    ICreditNoteService creditNotes,
    ILogger<GetPortalCreditNotesHandler> logger
) : IRequestHandler<GetPortalCreditNotesQuery, ErrorOr<List<CustomerCreditNote>>>
{
    public async Task<ErrorOr<List<CustomerCreditNote>>> Handle(
        GetPortalCreditNotesQuery request,
        CancellationToken cancellationToken)
    {
        var cardCodes = (await linkedAccounts.GetAllCardCodesAsync(request.PortalCardCode))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var response = await creditNotes.GetCustomerCreditNotesAsync(
            cardCodes, request.FromDate.Date, request.ToDate.Date, cancellationToken);

        if (response is null)
        {
            return Errors.CreditNote.LoadFailed("We could not load your credit notes just now. Please try again.");
        }

        var reachable = cardCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mine = response.CreditNotes.Where(note => reachable.Contains(note.CardCode)).ToList();

        if (mine.Count != response.CreditNotes.Count)
        {
            logger.LogWarning(
                "Dropped {Count} credit note(s) the API returned for accounts outside {PortalCardCode}'s login",
                response.CreditNotes.Count - mine.Count, request.PortalCardCode);
        }

        return mine;
    }
}
