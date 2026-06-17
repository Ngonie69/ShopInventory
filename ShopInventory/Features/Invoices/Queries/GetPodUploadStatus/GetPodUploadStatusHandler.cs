using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Crates;
using ShopInventory.Common.Mobile;
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
                .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

            if (currentUser is null)
                return Errors.Auth.UserNotFound;

            var isPodOperator = string.Equals(currentUser.Role, "PodOperator", StringComparison.OrdinalIgnoreCase);
            var isDriver = string.Equals(currentUser.Role, "Driver", StringComparison.OrdinalIgnoreCase);
            HashSet<string>? assignedCustomerCodes = null;

            if (isDriver)
            {
                var effectiveCustomerCodes = await MobileAssignedCustomerScope.GetEffectiveCustomerCodesAsync(
                    context,
                    currentUser,
                    logger,
                    cancellationToken);

                if (effectiveCustomerCodes.Count == 0)
                {
                    logger.LogWarning(
                        "Driver {Username} has no assigned customer codes; returning no POD report items",
                        currentUser.Username);

                    return BuildEmptyReport(request);
                }

                assignedCustomerCodes = effectiveCustomerCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var invoices = await sapClient.GetInvoiceHeadersByDateRangeAsync(
                request.FromDate,
                request.ToDate,
                PodExclusions.ExcludedCardCodes.ToList(),
                includeDocumentLines: isPodOperator,
                cancellationToken);

            if (assignedCustomerCodes is not null)
            {
                invoices = invoices
                    .Where(invoice =>
                        !string.IsNullOrWhiteSpace(invoice.CardCode) &&
                        assignedCustomerCodes.Contains(invoice.CardCode))
                    .ToList();
            }

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
            var cratePodLookup = await GetCratePodStatusByInvoiceDocNumsAsync(
                invoices.Select(invoice => invoice.DocNum).ToList(),
                cancellationToken);

            var items = invoices.Select(i =>
            {
                podLookup.TryGetValue(i.DocEntry, out var podInfo);
                cratePodLookup.TryGetValue(i.DocNum, out var cratePodInfo);
                var combinedPodInfo = MergePodStatusInfo(podInfo, cratePodInfo);
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
                    HasPod = combinedPodInfo is not null,
                    PodUploadedAt = combinedPodInfo?.UploadedAt,
                    PodUploadedBy = combinedPodInfo?.UploadedBy,
                    PodUploadedByUsers = combinedPodInfo?.UploadedByUsers
                        .Select(uploader => new PodUploadUserSummaryDto
                        {
                            Username = uploader.Username,
                            Role = uploader.Role,
                            AssignedSection = uploader.AssignedSection,
                            FileCount = uploader.FileCount,
                            LatestUploadedAt = uploader.LatestUploadedAt
                        })
                        .ToList() ?? new(),
                    PodCount = combinedPodInfo?.Count ?? 0
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

    private async Task<Dictionary<int, PodStatusInfo>> GetCratePodStatusByInvoiceDocNumsAsync(
        List<int> invoiceDocNums,
        CancellationToken cancellationToken)
    {
        var requestedDocNums = invoiceDocNums
            .Where(invoiceDocNum => invoiceDocNum > 0)
            .Distinct()
            .ToList();

        if (requestedDocNums.Count == 0)
        {
            return [];
        }

        var latestTransactions = await context.CrateTransactions
            .AsNoTracking()
            .Where(transaction =>
                EF.Functions.ILike(transaction.TransactionType, CrateTrackingConstants.TransactionTypeInvoice) &&
                transaction.InvoiceDocNum.HasValue &&
                requestedDocNums.Contains(transaction.InvoiceDocNum.Value))
            .OrderByDescending(transaction => transaction.EffectiveDate)
            .ThenByDescending(transaction => transaction.CreatedAt)
            .Select(transaction => new
            {
                transaction.Id,
                InvoiceDocNum = transaction.InvoiceDocNum!.Value
            })
            .ToListAsync(cancellationToken);

        var latestTransactionsByDocNum = latestTransactions
            .GroupBy(transaction => transaction.InvoiceDocNum)
            .ToDictionary(group => group.Key, group => group.First());

        if (latestTransactionsByDocNum.Count == 0)
        {
            return [];
        }

        var transactionIds = latestTransactionsByDocNum.Values
            .Select(transaction => transaction.Id)
            .ToList();

        var submissions = await context.CratePodSubmissions
            .AsNoTracking()
            .Where(submission => transactionIds.Contains(submission.CrateTransactionId))
            .Select(submission => new
            {
                submission.Id,
                submission.CrateTransactionId,
                Username = submission.SubmittedByUser != null ? submission.SubmittedByUser.Username : null,
                Role = submission.SubmittedByUser != null ? submission.SubmittedByUser.Role : null,
                AssignedSection = submission.SubmittedByUser != null ? submission.SubmittedByUser.AssignedSection : null
            })
            .ToListAsync(cancellationToken);

        if (submissions.Count == 0)
        {
            return [];
        }

        var submissionIds = submissions
            .Select(submission => submission.Id)
            .ToList();

        var attachments = await context.DocumentAttachments
            .AsNoTracking()
            .Where(attachment =>
                attachment.EntityType == CrateTrackingConstants.AttachmentEntityTypeCratePodSubmission &&
                submissionIds.Contains(attachment.EntityId))
            .Select(attachment => new
            {
                SubmissionId = attachment.EntityId,
                attachment.UploadedAt
            })
            .ToListAsync(cancellationToken);

        if (attachments.Count == 0)
        {
            return [];
        }

        var transactionDocNumsById = latestTransactionsByDocNum.Values
            .ToDictionary(transaction => transaction.Id, transaction => transaction.InvoiceDocNum);

        var cratePodData = submissions
            .GroupJoin(
                attachments,
                submission => submission.Id,
                attachment => attachment.SubmissionId,
                (submission, submissionAttachments) => new
                {
                    submission.CrateTransactionId,
                    submission.Username,
                    submission.Role,
                    submission.AssignedSection,
                    Attachments = submissionAttachments.ToList()
                })
            .Where(submission =>
                submission.Attachments.Count > 0 &&
                transactionDocNumsById.ContainsKey(submission.CrateTransactionId))
            .GroupBy(submission => transactionDocNumsById[submission.CrateTransactionId])
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var uploadedByUsers = group
                        .Select(submission =>
                        {
                            var latestUpload = submission.Attachments
                                .OrderByDescending(attachment => attachment.UploadedAt)
                                .First();

                            return new PodUploadUserSummaryInfo
                            {
                                Username = string.IsNullOrWhiteSpace(submission.Username)
                                    ? "Unknown uploader"
                                    : submission.Username.Trim(),
                                Role = string.IsNullOrWhiteSpace(submission.Role)
                                    ? null
                                    : submission.Role.Trim(),
                                AssignedSection = string.IsNullOrWhiteSpace(submission.AssignedSection)
                                    ? null
                                    : submission.AssignedSection.Trim(),
                                FileCount = submission.Attachments.Count,
                                LatestUploadedAt = latestUpload.UploadedAt
                            };
                        })
                        .GroupBy(summary => summary.Username, StringComparer.OrdinalIgnoreCase)
                        .Select(uploaderGroup =>
                        {
                            var latestUpload = uploaderGroup
                                .Where(summary => summary.LatestUploadedAt.HasValue)
                                .OrderByDescending(summary => summary.LatestUploadedAt)
                                .FirstOrDefault() ?? uploaderGroup.First();

                            return new PodUploadUserSummaryInfo
                            {
                                Username = uploaderGroup.Key,
                                Role = latestUpload.Role,
                                AssignedSection = latestUpload.AssignedSection,
                                FileCount = uploaderGroup.Sum(summary => summary.FileCount),
                                LatestUploadedAt = uploaderGroup.Max(summary => summary.LatestUploadedAt)
                            };
                        })
                        .OrderByDescending(summary => summary.LatestUploadedAt)
                        .ThenBy(summary => summary.Username)
                        .ToList();

                    var latestUploader = uploadedByUsers.FirstOrDefault();

                    return new PodStatusInfo
                    {
                        UploadedAt = group
                            .SelectMany(submission => submission.Attachments)
                            .Max(attachment => attachment.UploadedAt),
                        UploadedBy = latestUploader is null ||
                            string.Equals(latestUploader.Username, "Unknown uploader", StringComparison.OrdinalIgnoreCase)
                                ? null
                                : latestUploader.Username,
                        Count = group.Sum(submission => submission.Attachments.Count),
                        UploadedByUsers = uploadedByUsers
                    };
                });

        return cratePodData;
    }

    private static PodStatusInfo? MergePodStatusInfo(PodStatusInfo? invoicePodInfo, PodStatusInfo? cratePodInfo)
    {
        if (invoicePodInfo is null)
        {
            return cratePodInfo;
        }

        if (cratePodInfo is null)
        {
            return invoicePodInfo;
        }

        var uploadedByUsers = invoicePodInfo.UploadedByUsers
            .Concat(cratePodInfo.UploadedByUsers)
            .GroupBy(
                summary => string.IsNullOrWhiteSpace(summary.Username)
                    ? "Unknown uploader"
                    : summary.Username.Trim(),
                StringComparer.OrdinalIgnoreCase)
            .Select(uploaderGroup =>
            {
                var latestUpload = uploaderGroup
                    .Where(summary => summary.LatestUploadedAt.HasValue)
                    .OrderByDescending(summary => summary.LatestUploadedAt)
                    .FirstOrDefault() ?? uploaderGroup.First();

                return new PodUploadUserSummaryInfo
                {
                    Username = uploaderGroup.Key,
                    Role = latestUpload.Role,
                    AssignedSection = latestUpload.AssignedSection,
                    FileCount = uploaderGroup.Sum(summary => summary.FileCount),
                    LatestUploadedAt = uploaderGroup.Max(summary => summary.LatestUploadedAt)
                };
            })
            .OrderByDescending(summary => summary.LatestUploadedAt)
            .ThenBy(summary => summary.Username)
            .ToList();

        var latestUploader = uploadedByUsers.FirstOrDefault();
        var latestInvoicePod = invoicePodInfo.UploadedAt >= cratePodInfo.UploadedAt
            ? invoicePodInfo
            : cratePodInfo;

        return new PodStatusInfo
        {
            UploadedAt = invoicePodInfo.UploadedAt >= cratePodInfo.UploadedAt
                ? invoicePodInfo.UploadedAt
                : cratePodInfo.UploadedAt,
            UploadedBy = latestUploader?.Username is null ||
                string.Equals(latestUploader.Username, "Unknown uploader", StringComparison.OrdinalIgnoreCase)
                    ? latestInvoicePod.UploadedBy
                    : latestUploader.Username,
            Count = invoicePodInfo.Count + cratePodInfo.Count,
            UploadedByUsers = uploadedByUsers
        };
    }

    private static PodUploadStatusReportDto BuildEmptyReport(GetPodUploadStatusQuery request)
        => new()
        {
            FromDate = request.FromDate.ToString("yyyy-MM-dd"),
            ToDate = request.ToDate.ToString("yyyy-MM-dd"),
            TotalInvoices = 0,
            UploadedCount = 0,
            PendingCount = 0,
            Items = []
        };

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
