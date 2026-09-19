using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayment;

/// <summary>One daily incoming payment with its invoices.</summary>
public sealed record GetDailyIncomingPaymentQuery(int Id) : IRequest<ErrorOr<DailyIncomingPaymentDetail>>;
