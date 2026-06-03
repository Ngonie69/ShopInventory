using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Reports.Queries.GetAccountSalesPaymentReport;

public sealed record GetAccountSalesPaymentReportQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    AccountSalesPaymentGrouping Grouping,
    IReadOnlyList<string> AccountCodes
) : IRequest<ErrorOr<GetAccountSalesPaymentReportResult>>;