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

            var isPodOperator = string.Equals(currentUser.Role, "PodOperator", StringComparison.OrdinalIgnoreCase);

            var invoices = await sapClient.GetInvoiceHeadersByDateRangeAsync(
                request.FromDate,
                request.ToDate,
                PodExclusions.ExcludedCardCodes.ToList(),
                includeDocumentLines: isPodOperator,
                cancellationToken);

            if (isPodOperator)
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
                var creatorLocation = PodInvoiceCreatorLocations.GetCreatorLocation(i.UserSign);

                return new PodUploadStatusItemDto
                {
                    DocEntry = i.DocEntry,
                    DocNum = i.DocNum,
                    DocDate = i.DocDate,
                    CardCode = i.CardCode,
                    CardName = i.CardName,
                    DocTotal = i.DocTotal,
                    DocCurrency = i.DocCurrency,
                    CreatedByUserId = i.UserSign,
                    CreatedByUserCode = creatorLocation?.UserName,
                    CreatedLocation = creatorLocation?.Location,
                    HasPod = podInfo != null,
                    PodUploadedAt = podInfo?.UploadedAt,
                    PodUploadedBy = podInfo?.UploadedBy,
                    PodUploadedByUsers = podInfo?.UploadedByUsers
                        .Select(uploader => new PodUploadUserSummaryDto
                        {
                            Username = uploader.Username,
                            Role = uploader.Role,
                            AssignedSection = uploader.AssignedSection,
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
        var candidateDocEntries = invoices
            .Select(invoice => invoice.DocEntry)
            .Distinct()
            .ToList();
        var locallyScopedDocEntries = await GetLocalScopedInvoiceDocEntriesAsync(
            candidateDocEntries,
            normalizedSection,
            warehouseLocations,
            cancellationToken);

        var invoicesWithLines = invoices
            .Where(invoice => invoice.DocumentLines is { Count: > 0 })
            .ToList();

        var scopedDocEntries = invoicesWithLines
            .Where(invoice => PodLocationScope.InvoiceMatchesAssignedSection(invoice, normalizedSection, warehouseLocations))
            .Select(invoice => invoice.DocEntry)
            .ToHashSet();

        foreach (var docEntry in locallyScopedDocEntries)
        {
            scopedDocEntries.Add(docEntry);
        }

        foreach (var invoice in invoices)
        {
            var creatorLocation = PodInvoiceCreatorLocations.GetCreatorLocation(invoice.UserSign)?.Location;
            if (string.Equals(
                    PodLocationScope.CanonicalizeSection(creatorLocation),
                    PodLocationScope.CanonicalizeSection(normalizedSection),
                    StringComparison.OrdinalIgnoreCase))
            {
                scopedDocEntries.Add(invoice.DocEntry);
            }
        }

        var invoicesWithoutLines = invoices
            .Where(invoice => !scopedDocEntries.Contains(invoice.DocEntry))
            .Where(invoice => invoice.DocumentLines is null || invoice.DocumentLines.Count == 0)
            .Select(invoice => invoice.DocEntry)
            .ToList();

        if (invoicesWithoutLines.Count > 0)
        {
            var fallbackDocEntries = await documentService.GetScopedPodInvoiceDocEntriesAsync(
                invoicesWithoutLines,
                normalizedSection,
                cancellationToken);

            foreach (var docEntry in fallbackDocEntries)
            {
                scopedDocEntries.Add(docEntry);
            }
        }

        if (scopedDocEntries.Count == 0)
        {
            return [];
        }

        return invoices
            .Where(invoice => scopedDocEntries.Contains(invoice.DocEntry))
            .ToList();
    }

    private async Task<HashSet<int>> GetLocalScopedInvoiceDocEntriesAsync(
        List<int> docEntries,
        string assignedSection,
        IReadOnlyDictionary<string, string?> warehouseLocations,
        CancellationToken cancellationToken)
    {
        if (docEntries.Count == 0)
        {
            return [];
        }

        var localInvoiceWarehouseRows = await context.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.SAPDocEntry.HasValue && docEntries.Contains(invoice.SAPDocEntry.Value))
            .SelectMany(invoice => invoice.DocumentLines
                .Where(line => line.WarehouseCode != null && line.WarehouseCode != string.Empty)
                .Select(line => new
                {
                    DocEntry = invoice.SAPDocEntry!.Value,
                    line.WarehouseCode
                }))
            .ToListAsync(cancellationToken);

        return localInvoiceWarehouseRows
            .GroupBy(row => row.DocEntry)
            .Where(group => PodLocationScope.WarehouseCodesMatchAssignedSection(
                group.Select(row => row.WarehouseCode),
                assignedSection,
                warehouseLocations))
            .Select(group => group.Key)
            .ToHashSet();
    }
}
