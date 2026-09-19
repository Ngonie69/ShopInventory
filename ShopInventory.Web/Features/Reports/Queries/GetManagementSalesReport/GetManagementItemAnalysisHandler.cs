using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetManagementSalesReport;

public sealed class GetManagementItemAnalysisHandler(
    HttpClient httpClient,
    ILogger<GetManagementItemAnalysisHandler> logger
) : IRequestHandler<GetManagementItemAnalysisQuery, ErrorOr<ManagementItemAnalysisResult>>
{
    public Task<ErrorOr<ManagementItemAnalysisResult>> Handle(
        GetManagementItemAnalysisQuery request,
        CancellationToken cancellationToken) =>
        ManagementReportApi.GetAsync<ManagementItemAnalysisResult>(
            httpClient,
            logger,
            "api/DesktopIntegration/sales/management-report/item",
            "item analysis",
            [
                ("itemCode", request.ItemCode),
                ("fromDate", ManagementReportApi.Date(request.FromDate)),
                ("toDate", ManagementReportApi.Date(request.ToDate)),
                ("warehouseCode", request.WarehouseCode),
                ("sourceSystem", request.SourceSystem),
                ("cardCode", request.CardCode),
            ],
            cancellationToken);
}
