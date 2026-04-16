using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Payments.Queries.GetProviders;

public sealed class GetProvidersHandler(
    IPaymentGatewayService paymentService
) : IRequestHandler<GetProvidersQuery, ErrorOr<PaymentProvidersResponse>>
{
    public async Task<ErrorOr<PaymentProvidersResponse>> Handle(
        GetProvidersQuery request,
        CancellationToken cancellationToken)
    {
        var providers = await paymentService.GetAvailableProvidersAsync();
        return providers;
    }
}
