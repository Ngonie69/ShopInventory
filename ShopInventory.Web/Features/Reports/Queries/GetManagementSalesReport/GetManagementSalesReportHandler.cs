using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;

public sealed class GetManagementSalesReportHandler(
    HttpClient httpClient,
    ILogger<GetManagementSalesReportHandler> logger
) : IRequestHandler<GetManagementSalesReportQuery, ErrorOr<ManagementSalesReportResult>>
{
    public Task<ErrorOr<ManagementSalesReportResult>> Handle(
        GetManagementSalesReportQuery request,
        CancellationToken cancellationToken) =>
        ManagementReportApi.GetAsync<ManagementSalesReportResult>(
            httpClient,
            logger,
            "api/DesktopIntegration/sales/management-report",
            "management sales report",
            [
                ("fromDate", ManagementReportApi.Date(request.FromDate)),
                ("toDate", ManagementReportApi.Date(request.ToDate)),
                ("warehouseCode", request.WarehouseCode),
                ("sourceSystem", request.SourceSystem),
            ],
            cancellationToken);
}
