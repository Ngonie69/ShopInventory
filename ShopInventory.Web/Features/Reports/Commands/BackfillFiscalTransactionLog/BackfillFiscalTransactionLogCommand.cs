using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Commands.BackfillFiscalTransactionLog;

public sealed record BackfillFiscalTransactionLogCommand(
    DateTime? FromDate,
    DateTime? ToDate,
    int MaxInvoices = 500,
    int PageSize = 100) : IRequest<ErrorOr<BackfillFiscalTransactionLogResult>>;