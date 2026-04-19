using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Queries.GenerateEndOfDayReport;

public sealed class GenerateEndOfDayReportHandler(
    ApplicationDbContext context
) : IRequestHandler<GenerateEndOfDayReportQuery, ErrorOr<EndOfDayReportDto>>
{
    public async Task<ErrorOr<EndOfDayReportDto>> Handle(
        GenerateEndOfDayReportQuery query,
        CancellationToken cancellationToken)
    {
        var reportDate = query.ReportDate?.Date ?? DateTime.UtcNow.Date;

        var allSales = await context.DesktopSales
            .AsNoTracking()
            .Include(s => s.Consolidation)
            .Where(s => s.DocDate == reportDate)
            .OrderBy(s => s.CardCode)
            .ThenBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        if (allSales.Count == 0)
            return Errors.DesktopSales.ReportNotFound(reportDate);

        var consolidations = await context.SaleConsolidations
            .AsNoTracking()
            .Where(c => c.ConsolidationDate == reportDate)
            .ToDictionaryAsync(c => c.CardCode, cancellationToken);

        var report = new EndOfDayReportDto
        {
            ReportDate = reportDate,
            GeneratedAt = DateTime.UtcNow,
            TotalSalesCount = allSales.Count,
            TotalSalesAmount = allSales.Sum(s => s.TotalAmount),
            TotalVatAmount = allSales.Sum(s => s.VatAmount),
            TotalAmountPaid = allSales.Sum(s => s.AmountPaid)
        };

        // Group by BP
        var groups = allSales.GroupBy(s => s.CardCode);

        foreach (var group in groups)
        {
            var cardCode = group.Key;
            var sales = group.ToList();
            consolidations.TryGetValue(cardCode, out var consolidation);

            var bpSummary = new BPSummaryDto
            {
                CardCode = cardCode,
                CardName = sales.First().CardName,
                SalesCount = sales.Count,
                TotalAmount = sales.Sum(s => s.TotalAmount),
                TotalVat = sales.Sum(s => s.VatAmount),
                TotalPaid = sales.Sum(s => s.AmountPaid),
                IndividualSales = sales.Select(s => new IndividualSaleDto
                {
                    SaleId = s.Id,
                    ExternalReferenceId = s.ExternalReferenceId,
                    Amount = s.TotalAmount,
                    VatAmount = s.VatAmount,
                    FiscalReceiptNumber = s.FiscalReceiptNumber,
                    FiscalizationStatus = s.FiscalizationStatus.ToString(),
                    PaymentMethod = s.PaymentMethod,
                    AmountPaid = s.AmountPaid,
                    CreatedAt = s.CreatedAt
                }).ToList()
            };

            if (consolidation != null)
            {
                bpSummary.ConsolidatedInvoice = new ConsolidatedInvoiceDto
                {
                    SapDocEntry = consolidation.SapDocEntry,
                    SapDocNum = consolidation.SapDocNum,
                    PostedAt = consolidation.PostedAt,
                    Status = consolidation.Status.ToString()
                };

                if (consolidation.PaymentSapDocNum.HasValue)
                {
                    bpSummary.IncomingPayment = new ConsolidatedPaymentDto
                    {
                        SapDocNum = consolidation.PaymentSapDocNum,
                        SapDocEntry = consolidation.PaymentSapDocEntry,
                        Amount = sales.Sum(s => s.AmountPaid),
                        Status = consolidation.PaymentStatus
                    };
                }
            }

            report.BusinessPartnerSummaries.Add(bpSummary);
        }

        // Identify unposted sales
        var unposted = allSales
            .Where(s => s.ConsolidationStatus != DesktopSaleConsolidationStatus.Consolidated)
            .ToList();

        report.UnpostedSales = unposted.Select(s =>
        {
            var reason = s.ConsolidationStatus switch
            {
                DesktopSaleConsolidationStatus.Pending => "Not yet consolidated",
                DesktopSaleConsolidationStatus.Failed => s.Consolidation?.LastError ?? "SAP posting failed",
                DesktopSaleConsolidationStatus.Excluded => "Excluded from consolidation",
                _ => "Unknown"
            };

            return new UnpostedSaleDto
            {
                SaleId = s.Id,
                ExternalReferenceId = s.ExternalReferenceId,
                CardCode = s.CardCode,
                CardName = s.CardName,
                Amount = s.TotalAmount,
                FiscalReceiptNumber = s.FiscalReceiptNumber,
                ConsolidationStatus = s.ConsolidationStatus.ToString(),
                Reason = reason
            };
        }).ToList();

        report.PostedInvoiceCount = report.BusinessPartnerSummaries
            .Count(bp => bp.ConsolidatedInvoice?.SapDocNum != null);
        report.UnpostedInvoiceCount = report.UnpostedSales.Count;

        return report;
    }
}
