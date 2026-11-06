using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CreditNotes.Queries.GetCustomerCreditNotes;

/// <summary>
/// The credit notes on a set of customer accounts over a period, with their lines, as the customer
/// portal shows them.
/// </summary>
/// <param name="CardCodes">The accounts — one, or a multi-account customer's main and sub accounts.</param>
/// <param name="FromDate">First document date, inclusive.</param>
/// <param name="ToDate">Last document date, inclusive.</param>
public sealed record GetCustomerCreditNotesQuery(
    IReadOnlyList<string> CardCodes,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<CustomerCreditNotesResult>>;
