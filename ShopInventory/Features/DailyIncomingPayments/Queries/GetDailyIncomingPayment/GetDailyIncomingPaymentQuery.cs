using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DailyIncomingPayments.Queries.GetDailyIncomingPayment;

/// <summary>One daily incoming payment with the invoices it settles and the sales they came from.</summary>
public sealed record GetDailyIncomingPaymentQuery(int Id) : IRequest<ErrorOr<DailyIncomingPaymentDetailDto>>;
