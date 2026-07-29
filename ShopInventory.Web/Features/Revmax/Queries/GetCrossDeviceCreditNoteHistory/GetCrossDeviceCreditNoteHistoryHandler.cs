using ErrorOr;
using MediatR;
using ShopInventory.Web.Features.Reports.Queries.GetFiscalTransactionLog;

namespace ShopInventory.Web.Features.Revmax.Queries.GetCrossDeviceCreditNoteHistory;

public sealed class GetCrossDeviceCreditNoteHistoryHandler(IMediator mediator)
    : IRequestHandler<GetCrossDeviceCreditNoteHistoryQuery, ErrorOr<GetFiscalTransactionLogResult>>
{
    private const string DocumentType = "CreditNote";
    private const string SourceSystem = "RevmaxEndpoint";
    private const string ClientTransactionPrefix = "revmax-transactmext-";

    public Task<ErrorOr<GetFiscalTransactionLogResult>> Handle(
        GetCrossDeviceCreditNoteHistoryQuery request,
        CancellationToken cancellationToken)
        => mediator.Send(
            new GetFiscalTransactionLogQuery(
                request.FromDate,
                request.ToDate,
                request.Search,
                request.Status,
                DocumentType,
                SourceSystem,
                ClientTransactionPrefix,
                request.Page,
                request.PageSize),
            cancellationToken);
}