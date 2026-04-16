using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Payments.Queries.GetTransactions;

public sealed class GetTransactionsHandler(
    IPaymentGatewayService paymentService
) : IRequestHandler<GetTransactionsQuery, ErrorOr<PaymentTransactionListResponse>>
{
    public async Task<ErrorOr<PaymentTransactionListResponse>> Handle(
        GetTransactionsQuery request,
        CancellationToken cancellationToken)
    {
        var transactions = await paymentService.GetTransactionsAsync(
            request.Provider, request.Status, request.Page, request.PageSize);
        return transactions;
    }
}
