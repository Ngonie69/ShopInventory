using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Pods;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetPodUploadStatus;

public sealed class GetPodUploadStatusHandler(
    ISAPServiceLayerClient sapClient,
    IDocumentService documentService,
    ApplicationDbContext context,
    IOptions<SAPSettings> settings,
    ILogger<GetPodUploadStatusHandler> logger
) : IRequestHandler<GetPodUploadStatusQuery, ErrorOr<PodUploadStatusReportDto>>
{
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
            var currentUser = await context.Users
                .AsNoTracking()
                .Where(u => u.Id == request.UserId)
                .Select(u => new { u.Role, u.AssignedSection, u.Username })
                .FirstOrDefaultAsync(cancellationToken);

            if (currentUser is null)
                return Errors.Auth.UserNotFound;

            var invoices = await sapClient.GetInvoiceHeadersByDateRangeAsync(request.FromDate, request.ToDate, PodExclusions.ExcludedCardCodes.ToList(), cancellationToken);

            if (string.Equals(currentUser.Role, "PodOperator", StringComparison.OrdinalIgnoreCase))
            {
                invoices = await FilterInvoicesForPodOperatorAsync(
                    invoices,
                    currentUser.AssignedSection,
                    currentUser.Username,
                    cancellationToken);
            }

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
                    PodUploadedByUsers = podInfo?.UploadedByUsers
                        .Select(uploader => new PodUploadUserSummaryDto
                        {
                            Username = uploader.Username,
                            FileCount = uploader.FileCount,
                            LatestUploadedAt = uploader.LatestUploadedAt
                        })
                        .ToList() ?? new(),
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

    private async Task<List<Invoice>> FilterInvoicesForPodOperatorAsync(
        List<Invoice> invoices,
        string? assignedSection,
        string username,
        CancellationToken cancellationToken)
    {
        if (invoices.Count == 0)
        {
            return invoices;
        }

        if (string.IsNullOrWhiteSpace(assignedSection))
        {
            logger.LogWarning("PodOperator {Username} has no assigned section; returning no POD report items", username);
            return [];
        }

        var normalizedSection = assignedSection.Trim();
        var warehouseLocations = PodLocationScope.BuildWarehouseLocationLookup(
            await sapClient.GetWarehousesAsync(cancellationToken));

        return invoices
            .Where(invoice => PodLocationScope.InvoiceMatchesAssignedSection(invoice, normalizedSection, warehouseLocations))
            .ToList();
    }
}
