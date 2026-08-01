using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Invoices.Queries.GetOpenInvoicesByCustomers;

/// <summary>
/// Every invoice still carrying a balance for a set of accounts, in one SAP walk.
/// </summary>
/// <remarks>
/// Exists because the alternative the customer portal was using is unbounded. To show a customer
/// what they owe, the portal called the by-customer endpoint with no dates, which falls through to
/// "every invoice this account has ever had", paged 500 at a time — an old account's entire trading
/// history — and then discarded all but the handful still open. It did that once per linked
/// account, on the dashboard, the invoices page and the aging summary.
///
/// SAP can answer the actual question directly: filter on DocumentStatus server-side, and OR the
/// card codes into one filter rather than walking per account.
/// </remarks>
public sealed record GetOpenInvoicesByCustomersQuery(
    IReadOnlyList<string> CardCodes
) : IRequest<ErrorOr<InvoiceDateResponseDto>>;
