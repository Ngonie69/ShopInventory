using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Payments.Queries.GetTransactions;

public sealed record GetTransactionsQuery(
    string? Provider,
    string? Status,
    int Page,
    int PageSize
) : IRequest<ErrorOr<PaymentTransactionListResponse>>;
