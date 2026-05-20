using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;

public sealed record GetFiscalTransactionLogQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    string? Search,
    string? Status,
    string? DocumentType,
    int Page,
    int PageSize
) : IRequest<ErrorOr<GetFiscalTransactionLogResult>>;