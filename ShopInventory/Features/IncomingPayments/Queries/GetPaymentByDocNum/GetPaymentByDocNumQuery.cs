using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentByDocNum;

public sealed record GetPaymentByDocNumQuery(int DocNum) : IRequest<ErrorOr<IncomingPaymentDto>>;
