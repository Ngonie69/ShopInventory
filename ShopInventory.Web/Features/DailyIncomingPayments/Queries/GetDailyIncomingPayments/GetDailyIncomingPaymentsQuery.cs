using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayments;

/// <summary>The daily incoming payments for a range of trading days.</summary>
public sealed record GetDailyIncomingPaymentsQuery(DateTime From, DateTime To, string? CardCode, string? Status) : IRequest<ErrorOr<List<DailyIncomingPaymentSummary>>>;
