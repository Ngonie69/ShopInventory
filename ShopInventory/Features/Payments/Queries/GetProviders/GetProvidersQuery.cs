using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Payments.Queries.GetProviders;

public sealed record GetProvidersQuery() : IRequest<ErrorOr<PaymentProvidersResponse>>;
