using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConsolidateDailySales;

public sealed class ConsolidateDailySalesHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IBatchInventoryValidationService batchService,
    IOptions<DailyStockSettings> settings,
    ILogger<ConsolidateDailySalesHandler> logger
) : IRequestHandler<ConsolidateDailySalesCommand, ErrorOr<ConsolidateDailySalesResult>>
{
    public async Task<ErrorOr<ConsolidateDailySalesResult>> Handle(
        ConsolidateDailySalesCommand command,
        CancellationToken cancellationToken)
    {
        var consolidationDate = command.ConsolidationDate?.Date ?? DateTime.UtcNow.Date;

        // Get all pending sales for the date
        var pendingSales = await context.DesktopSales
            .Include(s => s.Lines)
            .Where(s => s.DocDate == consolidationDate &&
                        s.ConsolidationStatus == DesktopSaleConsolidationStatus.Pending)
            .ToListAsync(cancellationToken);

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

        return new ConsolidateDailySalesResult(
            consolidationDate,
            pendingSales.Count,
            successCount,
            failCount,
            results);
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
            .GroupBy(l => new { l.ItemCode, l.WarehouseCode, l.UnitPrice, l.TaxCode, l.DiscountPercent })
            .Select(g => new CreateInvoiceLineRequest
            {
                ItemCode = g.Key.ItemCode,
                Quantity = g.Sum(l => l.Quantity),
                UnitPrice = g.Key.UnitPrice,
                WarehouseCode = g.Key.WarehouseCode,
                TaxCode = g.Key.TaxCode,
                DiscountPercent = g.Key.DiscountPercent,
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
            // Post consolidated invoice to SAP (batch allocation happens here)
            var sapInvoice = await sapClient.CreateInvoiceAsync(invoiceRequest, ct);

            consolidation.SapDocEntry = sapInvoice.DocEntry;
            consolidation.SapDocNum = sapInvoice.DocNum;
            consolidation.PostedAt = DateTime.UtcNow;
            consolidation.Status = ConsolidationStatus.Posted;

            // Mark all sales as consolidated
            foreach (var sale in sales)
            {
                sale.ConsolidationStatus = DesktopSaleConsolidationStatus.Consolidated;
                sale.ConsolidationId = consolidation.Id;
            }

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
}
