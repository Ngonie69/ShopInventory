using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;

public sealed class ConsolidateDailySalesHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IBatchInventoryValidationService batchValidation,
    IInvoiceQueueService queueService,
    INotificationService notificationService,
    IHubContext<NotificationHub> hubContext,
    ISender sender,
    ILogger<ConsolidateDailySalesHandler> logger
) : IRequestHandler<ConsolidateDailySalesCommand, ErrorOr<ConsolidateDailySalesResult>>
{
    // Tracks queue entry IDs grouped by CardCode for post-consolidation marking
    private readonly Dictionary<string, List<int>> _queueIdsByCardCode = new();

    public async Task<ErrorOr<ConsolidateDailySalesResult>> Handle(
        ConsolidateDailySalesCommand command,
        CancellationToken cancellationToken)
    {
        var consolidationDate = command.ConsolidationDate?.Date ?? DateTime.UtcNow.Date;

        // Get all pending desktop sales for the date
        var pendingSales = await context.DesktopSales
            .Include(s => s.Lines)
            .Where(s => s.DocDate == consolidationDate &&
                        s.ConsolidationStatus == DesktopSaleConsolidationStatus.Pending)
            .ToListAsync(cancellationToken);

        // Also get fiscalized queued invoices for the date
        var fiscalizedQueueEntries = await queueService.GetFiscalizedInvoicesAsync(
            consolidationDate, cancellationToken);

        // Convert queue entries to DesktopSaleEntity-compatible format so they
        // can be consolidated together with direct desktop sales
        var queueSales = ConvertQueueEntriesToSales(fiscalizedQueueEntries);
        pendingSales.AddRange(queueSales);

        if (pendingSales.Count == 0)
            return Errors.DesktopSales.NoPendingSales;

        // Group by CardCode
        var groups = pendingSales
            .GroupBy(s => s.CardCode)
            .ToList();

        var results = new List<ConsolidationGroupResult>();
        var successCount = 0;
        var failCount = 0;

        foreach (var group in groups)
        {
            var cardCode = group.Key;
            var sales = group.ToList();
            var cardName = sales.First().CardName;

            try
            {
                var result = await ConsolidateGroupAsync(
                    cardCode, cardName, consolidationDate, sales, cancellationToken);
                results.Add(result);

                if (result.Status is "Posted" or "PartiallyCompleted")
                    successCount++;
                else
                    failCount++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to consolidate sales for {CardCode}", cardCode);
                failCount++;
                results.Add(new ConsolidationGroupResult(
                    cardCode, cardName, sales.Count,
                    sales.Sum(s => s.TotalAmount),
                    null, null, "Failed", ex.Message));
            }
        }

        var consolidationResult = new ConsolidateDailySalesResult(
            consolidationDate,
            pendingSales.Count,
            successCount,
            failCount,
            results);

        // Broadcast real-time event to connected Web clients
        await hubContext.Clients.Group("all").SendAsync("ConsolidationCompleted", new
        {
            ConsolidationDate = consolidationDate,
            TotalSales = pendingSales.Count,
            SuccessCount = successCount,
            FailCount = failCount
        });

        return consolidationResult;
    }

    private async Task<ConsolidationGroupResult> ConsolidateGroupAsync(
        string cardCode, string? cardName, DateTime consolidationDate,
        List<DesktopSaleEntity> sales, CancellationToken ct)
    {
        var totalAmount = sales.Sum(s => s.TotalAmount);
        var totalVat = sales.Sum(s => s.VatAmount);
        var totalPaid = sales.Sum(s => s.AmountPaid);

        // Create consolidation record
        var consolidation = new SaleConsolidationEntity
        {
            CardCode = cardCode,
            CardName = cardName,
            ConsolidationDate = consolidationDate,
            TotalAmount = totalAmount,
            TotalVat = totalVat,
            SaleCount = sales.Count,
            Status = ConsolidationStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        context.SaleConsolidations.Add(consolidation);
        await context.SaveChangesAsync(ct);

        // Merge line items across all sales for this BP
        var mergedLines = sales
            .SelectMany(s => s.Lines)
            .GroupBy(l => new { l.ItemCode, l.WarehouseCode, l.UnitPrice, l.TaxCode, l.DiscountPercent, l.CostCentreCode })
            .Select(g => new CreateInvoiceLineRequest
            {
                ItemCode = g.Key.ItemCode,
                Quantity = g.Sum(l => l.Quantity),
                UnitPrice = g.Key.UnitPrice,
                WarehouseCode = g.Key.WarehouseCode,
                TaxCode = g.Key.TaxCode,
                DiscountPercent = g.Key.DiscountPercent,
                CostCentreCode = g.Key.CostCentreCode,
                AutoAllocateBatches = true
            })
            .ToList();

        // Build the SAP invoice request
        var saleRefs = string.Join(",", sales.Select(s => s.ExternalReferenceId));
        var invoiceRequest = new CreateInvoiceRequest
        {
            CardCode = cardCode,
            DocDate = consolidationDate.ToString("yyyy-MM-dd"),
            DocDueDate = consolidationDate.ToString("yyyy-MM-dd"),
            NumAtCard = $"CONSOL-{consolidationDate:yyyyMMdd}-{cardCode}",
            Comments = $"Consolidated {sales.Count} desktop sale(s): {saleRefs}",
            DocCurrency = sales.First().Currency,
            U_Van_saleorder = $"CONSOL-{consolidationDate:yyyyMMdd}-{cardCode}",
            Lines = mergedLines
        };

        try
        {
            var batchValidationResult = await batchValidation.ValidateAndAllocateBatchesAsync(
                invoiceRequest,
                autoAllocate: true,
                BatchAllocationStrategy.FEFO,
                ct);

            if (!batchValidationResult.IsValid)
            {
                throw new InvalidOperationException(
                    $"Consolidated invoice stock validation failed: {string.Join("; ", batchValidationResult.ValidationErrors.Select(error => error.Message))}");
            }

            ApplyAllocatedBatchesToRequest(invoiceRequest, batchValidationResult.AllocatedLines);

            var stockValidationErrors = await sapClient.ValidateStockAvailabilityAsync(invoiceRequest, ct);
            if (stockValidationErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Consolidated invoice stock validation failed: {string.Join("; ", stockValidationErrors.Select(error => error.Message))}");
            }

            var sapInvoice = await sapClient.CreateInvoiceAsync(invoiceRequest, ct);

            consolidation.SapDocEntry = sapInvoice.DocEntry;
            consolidation.SapDocNum = sapInvoice.DocNum;
            consolidation.PostedAt = DateTime.UtcNow;
            consolidation.Status = ConsolidationStatus.Posted;

            // Written with the DocNum, because this is what tells every later reader that fiscalising
            // this invoice would duplicate receipts already lodged with FDMS. See
            // ConsolidatedInvoiceRegistry.
            var constituentReceipts = DescribeConstituentReceipts(sales);
            consolidation.ConstituentFiscalReceipts = JsonSerializer.Serialize(constituentReceipts);

            // Mark all desktop sales as consolidated
            foreach (var sale in sales)
            {
                sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
                sale.ConsolidationId = consolidation.Id;
            }

            // Before anything else that can fail. The invoice exists in SAP from here on, and the
            // marker is the only thing standing between it and a second FDMS submission.
            await context.SaveChangesAsync(ct);

            // Mark queue entries as completed after successful SAP posting
            if (_queueIdsByCardCode.TryGetValue(cardCode, out var queueIds) && queueIds.Count > 0)
            {
                await queueService.MarkAsConsolidatedAsync(
                    queueIds, sapInvoice.DocEntry.ToString(), sapInvoice.DocNum, ct);
            }

            await RecordConsolidatedInvoiceAsFiscalisedAsync(
                consolidation,
                constituentReceipts,
                sales.First().Currency,
                ct);

            await NotifyVanSalesRecipientsAsync(
                sales,
                sapInvoice.DocEntry,
                sapInvoice.DocNum,
                cardCode,
                cardName,
                ct);

            logger.LogInformation(
                "Posted consolidated invoice for {CardCode}: SapDocNum={DocNum}, {SaleCount} sales, total={Total}",
                cardCode, sapInvoice.DocNum, sales.Count, totalAmount);

            // Post incoming payment if any amount was paid
            int? paymentDocNum = null;
            if (totalPaid > 0)
            {
                try
                {
                    paymentDocNum = await PostIncomingPaymentAsync(
                        cardCode, consolidationDate, totalPaid, sapInvoice.DocEntry, sales, ct);

                    consolidation.PaymentSapDocNum = paymentDocNum;
                    consolidation.PaymentPostedAt = DateTime.UtcNow;
                    consolidation.PaymentStatus = "Posted";
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to post payment for {CardCode}", cardCode);
                    consolidation.PaymentStatus = "Failed";
                    consolidation.Status = ConsolidationStatus.PartiallyCompleted;
                    consolidation.LastError = $"Payment failed: {ex.Message}";
                }
            }

            await context.SaveChangesAsync(ct);

            return new ConsolidationGroupResult(
                cardCode, cardName, sales.Count, totalAmount,
                sapInvoice.DocNum, paymentDocNum,
                consolidation.Status.ToString(), null);
        }
        catch (Exception ex)
        {
            consolidation.Status = ConsolidationStatus.Failed;
            consolidation.LastError = ex.Message;
            await context.SaveChangesAsync(ct);

            throw;
        }
    }

    /// <summary>
    /// Names the receipts each constituent sale was fiscalised under, before SAP.
    /// </summary>
    /// <remarks>
    /// The two sources fiscalise under different numbers, and the difference matters because that
    /// number is what the fiscalisation platform keys its duplicate guard on. A sale created through
    /// CreateDesktopSale goes to FDMS under its own row id; a queued invoice fiscalised by
    /// InvoicePostingJob goes under its external reference. The queue-derived sales here are
    /// transient objects built by <see cref="ConvertQueueEntriesToSales"/> and never persisted, so a
    /// zero id is what distinguishes them.
    /// </remarks>
    private static List<ConsolidatedFiscalReceipt> DescribeConstituentReceipts(
        IEnumerable<DesktopSaleEntity> sales)
        => sales
            .Select(sale => new ConsolidatedFiscalReceipt(
                sale.ExternalReferenceId,
                sale.Id > 0 ? sale.Id.ToString() : sale.ExternalReferenceId,
                sale.FiscalReceiptNumber,
                sale.TotalAmount))
            .ToList();

    /// <summary>
    /// Records the consolidated invoice as already fiscalised, so it never reports as "Unknown" and
    /// is never offered to the backfill queue or the Fiscalise button.
    /// </summary>
    /// <remarks>
    /// Swallowed on failure on purpose: the SAP invoice is already posted and the marker on the
    /// consolidation row is already committed, so nothing here is worth failing a consolidation that
    /// succeeded. The guards read the marker, not this row — this row is what explains the verdict
    /// to whoever reads the fiscal transaction log.
    /// </remarks>
    private async Task RecordConsolidatedInvoiceAsFiscalisedAsync(
        SaleConsolidationEntity consolidation,
        IReadOnlyCollection<ConsolidatedFiscalReceipt> receipts,
        string? currency,
        CancellationToken ct)
    {
        try
        {
            await InvoiceFiscalTransactionSync.RecordConsolidatedInvoiceAsync(
                sender,
                consolidation,
                receipts,
                currency,
                logger,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Could not record consolidated invoice {DocNum} as fiscalised. Its {SaleCount} sale(s) are already "
                + "with FDMS and it must not be fiscalised",
                consolidation.SapDocNum,
                consolidation.SaleCount);
        }
    }

    private static void ApplyAllocatedBatchesToRequest(
        CreateInvoiceRequest request,
        List<AllocatedBatchLine> allocatedLines)
    {
        if (request.Lines == null)
            return;

        foreach (var allocatedLine in allocatedLines)
        {
            var lineIndex = allocatedLine.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= request.Lines.Count)
                continue;

            var requestLine = request.Lines[lineIndex];

            if (requestLine.SerialNumbers is not { Count: > 0 } && allocatedLine.Serials.Count > 0)
            {
                requestLine.SerialNumbers = allocatedLine.Serials
                    .Select(serial => new SerialNumberRequest
                    {
                        InternalSerialNumber = serial.InternalSerialNumber,
                        SystemSerialNumber = serial.SystemSerialNumber
                    })
                    .ToList();
            }

            if (requestLine.BatchNumbers is { Count: > 0 } || allocatedLine.Batches.Count == 0)
                continue;

            requestLine.BatchNumbers = allocatedLine.Batches
                .Select(batch => new BatchNumberRequest
                {
                    BatchNumber = batch.BatchNumber,
                    Quantity = batch.QuantityAllocated,
                    ExpiryDate = batch.ExpiryDate
                })
                .ToList();
        }
    }

    private async Task NotifyVanSalesRecipientsAsync(
        IReadOnlyCollection<DesktopSaleEntity> sales,
        int sapDocEntry,
        int sapDocNum,
        string cardCode,
        string? cardName,
        CancellationToken cancellationToken)
    {
        var createdByValues = sales
            .Where(sale => string.Equals(sale.SourceSystem, "KefalosVanSales", StringComparison.OrdinalIgnoreCase))
            .Select(sale => sale.CreatedBy)
            .Where(createdBy => !string.IsNullOrWhiteSpace(createdBy))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (createdByValues.Count == 0)
        {
            return;
        }

        var invoiceDto = new InvoiceDto
        {
            DocEntry = sapDocEntry,
            DocNum = sapDocNum,
            CardCode = cardCode,
            CardName = cardName
        };

        foreach (var createdBy in createdByValues)
        {
            var (targetUserId, targetUsername) = await ResolveNotificationRecipientAsync(createdBy!, cancellationToken);
            if (string.IsNullOrWhiteSpace(targetUsername))
            {
                continue;
            }

            try
            {
                var notification = WorkflowNotificationFactory.CreateInvoiceCreatedNotification(
                    targetUserId,
                    targetUsername,
                    invoiceDto,
                    null,
                    "/mobile-drafts",
                    null);

                await notificationService.CreateNotificationAsync(notification, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to notify van-sales user {Username} about consolidated SAP invoice {DocNum}",
                    targetUsername,
                    sapDocNum);
            }
        }
    }

    private async Task<(Guid? UserId, string? Username)> ResolveNotificationRecipientAsync(
        string createdBy,
        CancellationToken cancellationToken)
    {
        var normalizedCreatedBy = createdBy.Trim();

        if (Guid.TryParse(normalizedCreatedBy, out var userId))
        {
            var username = await context.Users
                .AsNoTracking()
                .Where(user => user.Id == userId)
                .Select(user => user.Username)
                .FirstOrDefaultAsync(cancellationToken);

            return (userId, username);
        }

        var user = await context.Users
            .AsNoTracking()
            .Where(user => user.Username.ToUpper() == normalizedCreatedBy.ToUpper())
            .Select(candidate => new { candidate.Id, candidate.Username })
            .FirstOrDefaultAsync(cancellationToken);

        return user is null
            ? (null, normalizedCreatedBy)
            : (user.Id, user.Username);
    }

    private async Task<int?> PostIncomingPaymentAsync(
        string cardCode, DateTime date, decimal amount, int invoiceDocEntry,
        List<DesktopSaleEntity> sales, CancellationToken ct)
    {
        // Determine payment method from the majority of sales
        var primaryMethod = sales
            .Where(s => !string.IsNullOrEmpty(s.PaymentMethod))
            .GroupBy(s => s.PaymentMethod)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key ?? "Cash";

        var paymentRequest = new CreateIncomingPaymentRequest
        {
            CardCode = cardCode,
            DocDate = date.ToString("yyyy-MM-dd"),
            Remarks = $"Consolidated payment for {sales.Count} desktop sale(s) on {date:yyyy-MM-dd}",
            PaymentInvoices = new List<PaymentInvoiceRequest>
            {
                new()
                {
                    DocEntry = invoiceDocEntry,
                    SumApplied = amount
                }
            }
        };

        // Set the appropriate payment sum based on method
        switch (primaryMethod.ToLowerInvariant())
        {
            case "cash":
                paymentRequest.CashSum = amount;
                break;
            case "transfer":
            case "ecocash":
            case "innbucks":
            case "paynow":
                paymentRequest.TransferSum = amount;
                paymentRequest.TransferReference = string.Join(",",
                    sales.Where(s => !string.IsNullOrEmpty(s.PaymentReference))
                         .Select(s => s.PaymentReference));
                paymentRequest.TransferDate = date.ToString("yyyy-MM-dd");
                break;
            default:
                paymentRequest.CashSum = amount;
                break;
        }

        var payment = await sapClient.CreateIncomingPaymentAsync(paymentRequest, ct);

        logger.LogInformation(
            "Posted incoming payment for {CardCode}: DocNum={DocNum}, Amount={Amount}",
            cardCode, payment.DocNum, amount);

        return payment.DocNum;
    }

    /// <summary>
    /// Converts fiscalized queue entries into transient DesktopSaleEntity objects
    /// so they can be consolidated alongside direct desktop sales.
    /// These entities are NOT tracked by EF Core.
    /// </summary>
    private List<DesktopSaleEntity> ConvertQueueEntriesToSales(List<InvoiceQueueEntity> queueEntries)
    {
        var sales = new List<DesktopSaleEntity>();
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        foreach (var entry in queueEntries)
        {
            try
            {
                var request = JsonSerializer.Deserialize<CreateStockReservationRequest>(
                    entry.InvoicePayload, jsonOptions);

                if (request == null) continue;

                var sale = new DesktopSaleEntity
                {
                    ExternalReferenceId = entry.ExternalReference ?? entry.Id.ToString(),
                    SourceSystem = entry.SourceSystem,
                    CardCode = entry.CustomerCode ?? request.CardCode,
                    CardName = request.CardName,
                    DocDate = entry.CreatedAt.Date,
                    SalesPersonCode = request.SalesPersonCode,
                    TotalAmount = entry.TotalAmount,
                    VatAmount = 0, // VAT calculated by SAP
                    Currency = entry.Currency ?? "ZWG",
                    WarehouseCode = entry.WarehouseCode ?? request.Lines.FirstOrDefault()?.WarehouseCode ?? "",
                    PaymentMethod = "Cash",
                    AmountPaid = entry.TotalAmount,
                    CreatedAt = entry.CreatedAt,
                    CreatedBy = entry.CreatedBy,
                    // Mark as already fiscalized
                    FiscalizationStatus = DesktopSaleFiscalizationStatus.Success,
                    FiscalReceiptNumber = entry.FiscalReceiptNumber,
                    FiscalDeviceNumber = entry.FiscalDeviceNumber,
                    Lines = request.Lines.Select((l, idx) => new DesktopSaleLineEntity
                    {
                        LineNum = l.LineNum > 0 ? l.LineNum : idx,
                        ItemCode = l.ItemCode,
                        ItemDescription = l.ItemDescription,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice,
                        LineTotal = l.Quantity * l.UnitPrice,
                        WarehouseCode = l.WarehouseCode,
                        TaxCode = l.TaxCode,
                        DiscountPercent = l.DiscountPercent,
                        UoMCode = l.UoMCode,
                        CostCentreCode = l.CostCentreCode
                    }).ToList()
                };

                sales.Add(sale);

                // Track queue entry ID for post-consolidation marking
                if (!_queueIdsByCardCode.TryGetValue(sale.CardCode, out var ids))
                {
                    ids = new List<int>();
                    _queueIdsByCardCode[sale.CardCode] = ids;
                }
                ids.Add(entry.Id);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to convert queue entry {QueueId} to sale, skipping", entry.Id);
            }
        }

        return sales;
    }
}
