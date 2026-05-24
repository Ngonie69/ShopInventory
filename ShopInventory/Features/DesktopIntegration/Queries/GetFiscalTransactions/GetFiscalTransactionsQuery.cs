using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetFiscalTransactions;

public sealed record GetFiscalTransactionsQuery(
    string? Search,
    string? Status,
    string? DocumentType,
    string? SourceSystem,
    DateTime? FromUtc,
    DateTime? ToUtc,
    int Page = 1,
    int PageSize = 50
) : IRequest<ErrorOr<GetFiscalTransactionsResult>>;