using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetCreditReturns;

/// <summary>
/// The units credit notes put back on a warehouse's stock ledger over a run of ledger days, one row per
/// item of each credit.
/// </summary>
/// <param name="FromDate">First ledger day, inclusive.</param>
/// <param name="ToDate">Last ledger day, inclusive.</param>
public sealed record GetCreditReturnsQuery(
    string WarehouseCode,
    DateTime FromDate,
    DateTime ToDate
) : IRequest<ErrorOr<List<CreditReturnDto>>>;

/// <param name="LedgerDay">
/// The ledger day whose figure the return raised — the day in force when the credit was accepted, not
/// the day of the sale it reverses.
/// </param>
/// <param name="ReturnedAt">When the units went back on the ledger, in UTC.</param>
/// <param name="Quantity">How many came back. Always positive.</param>
/// <param name="CreditNoteNumber">The number a person quotes the credit by — <c>CN1753</c>.</param>
/// <param name="SaleNumber">The sale it reverses, as its receipt printed it — <c>INV1753</c>.</param>
/// <param name="SaleReference">That sale's own reference, which is what the till's sale list is keyed by.</param>
/// <param name="SaleDate">The day that sale was rung up.</param>
public sealed record CreditReturnDto(
    DateTime LedgerDay,
    DateTime ReturnedAt,
    string ItemCode,
    string? ItemDescription,
    string WarehouseCode,
    decimal Quantity,
    string CreditNoteNumber,
    string? SaleNumber,
    string? SaleReference,
    DateTime? SaleDate,
    string? Reason);
