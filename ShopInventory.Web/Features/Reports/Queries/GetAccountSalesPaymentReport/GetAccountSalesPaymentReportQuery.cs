using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed record GetAccountSalesPaymentReportQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    AccountSalesPaymentGrouping Grouping,
    string AccountCodesText
) : IRequest<ErrorOr<GetAccountSalesPaymentReportResult>>;