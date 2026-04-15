using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.IncomingPayments.Queries.GetPaymentByDocEntry;

public sealed record GetPaymentByDocEntryQuery(int DocEntry) : IRequest<ErrorOr<IncomingPaymentDto>>;
