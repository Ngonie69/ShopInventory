using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;

public sealed class GetPodUploadStatusHandler(
    ISAPServiceLayerClient sapClient,
    IDocumentService documentService,
    IOptions<SAPSettings> settings,
    ILogger<GetPodUploadStatusHandler> logger
) : IRequestHandler<GetPodUploadStatusQuery, ErrorOr<PodUploadStatusReportDto>>
{
    private static readonly HashSet<string> ExcludedPodCardCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CIS006", "MAC009", "MAC006", "COR007", "COR006", "COR008",
        "VAN008", "VAN009", "VAN010", "VAN011", "VAN012", "VAN013",
        "VAN014", "VAN015", "VAN016", "VAN017", "VAN018", "VAN019", "VAN020",
        "STA040", "STA041", "STA042", "STA043", "STA044", "STA045", "STA046", "STA047", "STA048",
        "PRO030", "PRO031", "PRO032", "PRO033", "PRO034", "PRO035", "PRO036",
        "CAS004(FCA)", "DON004", "TEA006", "TEA007"
    };

    public async Task<ErrorOr<PodUploadStatusReportDto>> Handle(
        GetPodUploadStatusQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        if (request.FromDate > request.ToDate)
            return Errors.Invoice.InvalidDateRange;

        try
        {
            var invoices = await sapClient.GetInvoiceHeadersByDateRangeAsync(request.FromDate, request.ToDate, ExcludedPodCardCodes.ToList(), cancellationToken);

            var docEntries = invoices.Select(i => i.DocEntry).ToList();
            var podLookup = await documentService.GetPodStatusByDocEntriesAsync(docEntries, cancellationToken);

            var items = invoices.Select(i =>
            {
                podLookup.TryGetValue(i.DocEntry, out var podInfo);
                return new PodUploadStatusItemDto
                {
                    DocEntry = i.DocEntry,
                    DocNum = i.DocNum,
                    DocDate = i.DocDate,
                    CardCode = i.CardCode,
                    CardName = i.CardName,
                    DocTotal = i.DocTotal,
                    DocCurrency = i.DocCurrency,
                    HasPod = podInfo != null,
                    PodUploadedAt = podInfo?.UploadedAt,
                    PodUploadedBy = podInfo?.UploadedBy,
                    PodCount = podInfo?.Count ?? 0
                };
            }).OrderByDescending(i => i.DocNum).ToList();

            return new PodUploadStatusReportDto
            {
                FromDate = request.FromDate.ToString("yyyy-MM-dd"),
                ToDate = request.ToDate.ToString("yyyy-MM-dd"),
                TotalInvoices = items.Count,
                UploadedCount = items.Count(i => i.HasPod),
                PendingCount = items.Count(i => !i.HasPod),
                Items = items
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating POD upload status report");
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
