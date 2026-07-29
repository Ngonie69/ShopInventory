using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Crates.Queries.GetCrateTransactions;

public sealed record GetCrateTransactionsQuery(
    string? Search,
    string? Status,
    string? TransactionType,
    Guid UserId
) : IRequest<ErrorOr<List<CrateTransactionDto>>>;