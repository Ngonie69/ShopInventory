using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.CustomerPortal.Queries.GetPortalCreditNotes;

/// <summary>The credit notes a portal login may see over a period, across every account it reaches.</summary>
/// <param name="PortalCardCode">The account the customer signed in as.</param>
/// <param name="FromDate">First document date, inclusive.</param>
/// <param name="ToDate">Last document date, inclusive.</param>
public sealed record GetPortalCreditNotesQuery(
    string PortalCardCode,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<List<CustomerCreditNote>>>;
