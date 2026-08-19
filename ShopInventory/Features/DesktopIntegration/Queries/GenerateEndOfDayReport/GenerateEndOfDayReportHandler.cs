using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.DesktopIntegration.Queries.GenerateEndOfDayReport;

/// <summary>
/// The day's cash reconciliation: what was sold, what was taken, and which of it has reached SAP.
/// </summary>
/// <remarks>
/// Every figure on it is a sum over <c>DesktopSales</c>, so the set of rows it reads <i>is</i> the report.
/// It reads the sales this server captured and still owes SAP a document for — the till sources and the
/// offline van batch — and it deliberately excludes <see cref="SaleSourceSystems.VanSalesOnline"/>. See
/// the filter below.
/// </remarks>
public sealed class GenerateEndOfDayReportHandler(
    ApplicationDbContext context
) : IRequestHandler<GenerateEndOfDayReportQuery, ErrorOr<EndOfDayReportDto>>
{
    public async Task<ErrorOr<EndOfDayReportDto>> Handle(
        GenerateEndOfDayReportQuery query,
        CancellationToken cancellationToken)
    {
        var reportDate = query.ReportDate?.Date ?? DateTime.UtcNow.Date;

        // Stated rather than assumed, because this handler had no source filter at all and so silently
        // absorbed a new source the day one was added.
        //
        // An online van sale's row on this table is not a sale — it is a carrier for the ZIMRA receipt the
        // handset signed, written under its own source precisely so that nothing counting money reads it.
        // The sale itself is already here twice over: as the confirmed StockReservation the van reports
        // count, and as the SAP invoice that reservation posted inside the request. Counting the carrier
        // as well adds a sale that was never in this report, inflates TotalSalesAmount and TotalVatAmount
        // by money already counted elsewhere, and lists the row under UnpostedSales' opposite — a
        // Consolidated row for an invoice this report played no part in posting.
        //
        // The money is the sharper half. See SaleSourceSystems.VanSalesOnline for the general rule.
        var allSales = await context.DesktopSales
            .AsNoTracking()
            .Include(s => s.Consolidation)
            .Where(s => s.DocDate == reportDate)
            .Where(s => s.SourceSystem != SaleSourceSystems.VanSalesOnline)
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
