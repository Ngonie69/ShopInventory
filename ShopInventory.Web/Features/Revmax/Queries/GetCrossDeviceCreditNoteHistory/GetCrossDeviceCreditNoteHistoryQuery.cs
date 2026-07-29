using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;

namespace ShopInventory.Web.Features.Revmax.Queries.GetCrossDeviceCreditNoteHistory;

public sealed record GetCrossDeviceCreditNoteHistoryQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    string? Search,
    string? Status,
    int Page,
    int PageSize) : IRequest<ErrorOr<GetFiscalTransactionLogResult>>;