using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayments;

/// <summary>
/// The daily incoming payments in a range of trading days, newest first.
/// </summary>
/// <param name="From">First trading day, inclusive.</param>
/// <param name="To">Last trading day, inclusive.</param>
/// <param name="CardCode">Only this business partner, when set.</param>
/// <param name="Status">Only this status, when set.</param>
public sealed record GetDailyIncomingPaymentsQuery(
    DateTime From,
    DateTime To,
    string? CardCode,
    string? Status
) : IRequest<ErrorOr<List<DailyIncomingPaymentSummaryDto>>>;
