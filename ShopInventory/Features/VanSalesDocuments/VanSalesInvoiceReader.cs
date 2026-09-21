using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesDocuments;

/// <summary>
/// One van invoice, with what the list does not show but the detail and the credit notes need.
/// </summary>
internal sealed record VanSalesInvoiceRecord(
    VanSalesInvoiceRow Row,
    string? ReservationId,
    int? DesktopSaleId,
    string? FiscalQrCode,
    int PostingAttempts,
    string? QueueStatus,
    VanSalesInvoiceSaleFacts? Sale);

/// <summary>
/// The columns the on-request post and fiscalisation rules read, from the sale row an invoice has: its
/// receipt row for an online sale, the sale itself for an offline one. Null for a converted order, whose
/// receipt lives on its queue entry alone.
/// </summary>
internal sealed record VanSalesInvoiceSaleFacts(
    string? SourceSystem,
    DesktopSaleConsolidationStatus ConsolidationStatus,
    DesktopSaleFiscalizationStatus FiscalizationStatus,
    bool RequiresReconciliation,
    DateTime CreatedAtUtc);

/// <summary>
/// Assembles the invoices the van sales app created, from the three places they are recorded.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Reservations under <see cref="SaleSourceSystems.VanSales"/></b> — an online sale and a converted
/// order both post from one. The reservation is the sale; everything else about it is joined on.</item>
/// <item><b>Receipt rows under <see cref="SaleSourceSystems.VanSalesOnline"/></b> — the fiscal receipt an
/// online sale carries, joined by van order. Never listed on their own: each is a reservation's receipt,
/// and listing it as well would show every sale twice.</item>
/// <item><b>Offline sales under <see cref="SaleSourceSystems.VanSales"/></b> on <c>DesktopSales</c> — sold
/// without signal, uploaded, and posted by the end-of-day run.</item>
/// </list>
/// Where a sale has no receipt row of its own — an online sale from before receipts were stored — the fiscal
/// log is asked by DocNum through <see cref="FiscalDocumentStatusProjector"/>, the same rule the invoice list
/// and the fiscalisation console use, so the three cannot disagree about one document.
/// </remarks>
internal static class VanSalesInvoiceReader
{
    public static async Task<List<VanSalesInvoiceRecord>> LoadAsync(
        ApplicationDbContext db,
        DateTime fromDay,
        DateTime toDay,
        string? reference,
        CancellationToken cancellationToken)
    {
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(fromDay, toDay);

        var reservationQuery = db.StockReservations
            .AsNoTracking()
            .Where(r => r.SourceSystem == SaleSourceSystems.VanSales);

        reservationQuery = reference is null
            ? reservationQuery.Where(r => r.CreatedAt >= windowStartUtc && r.CreatedAt < windowEndUtc)
            : reservationQuery.Where(r => r.ExternalReferenceId == reference);

        var reservations = await reservationQuery
            .Select(r => new
            {
                r.ReservationId,
                r.ExternalReferenceId,
                r.Status,
                r.CreatedAt,
                r.CreatedBy,
                r.CardCode,
                r.CardName,
                r.RouteCustomerCode,
                r.RouteCustomerName,
                r.PaymentMethod,
                r.TotalValue,
                r.Currency,
                r.SAPDocEntry,
                r.SAPDocNum,
                WarehouseCode = r.Lines.Select(l => l.WarehouseCode).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var references = reservations.Select(r => r.ExternalReferenceId).Distinct().ToList();
        var reservationIds = reservations.Select(r => r.ReservationId).ToList();

        var receipts = references.Count == 0
            ? []
            : await db.DesktopSales
                .AsNoTracking()
                .Where(s => s.SourceSystem == SaleSourceSystems.VanSalesOnline
                            && references.Contains(s.ExternalReferenceId))
                .Select(s => new ReceiptRow(
                    s.Id,
                    s.ExternalReferenceId,
                    s.FiscalizationStatus,
                    s.FiscalizationRequiresReconciliation,
                    s.FiscalError,
                    s.FiscalReceiptNumber,
                    s.FiscalVerificationCode,
                    s.FiscalQRCode,
                    s.FiscalDayNo,
                    s.FiscalDeviceNumber,
                    s.TotalAmount,
                    s.VatAmount,
                    s.SapDocEntry,
                    s.SapDocNum,
                    s.PostingAttempts,
                    s.LastPostingError,
                    s.ConsolidationStatus,
                    s.CreatedAt))
                .ToListAsync(cancellationToken);

        var receiptByReference = receipts
            .GroupBy(r => r.Reference)
            .ToDictionary(g => g.Key, g => g.First());

        var queued = reservationIds.Count == 0
            ? []
            : await db.InvoiceQueue
                .AsNoTracking()
                .Where(q => reservationIds.Contains(q.ReservationId))
                .Select(q => new { q.ReservationId, q.Status, q.LastError, q.FiscalReceiptNumber, q.SapDocEntry, q.SapDocNum })
                .ToListAsync(cancellationToken);

        var queueByReservation = queued
            .GroupBy(q => q.ReservationId)
            .ToDictionary(g => g.Key, g => g.First());

        var offlineQuery = db.DesktopSales
            .AsNoTracking()
            .Where(s => s.SourceSystem == SaleSourceSystems.VanSales);

        offlineQuery = reference is null
            ? offlineQuery.Where(s => s.DocDate >= fromDay.Date && s.DocDate <= toDay.Date)
            : offlineQuery.Where(s => s.ExternalReferenceId == reference);

        var offline = await offlineQuery
            .Select(s => new
            {
                s.Id,
                s.ExternalReferenceId,
                s.DocDate,
                s.CreatedAt,
                s.CreatedBy,
                s.WarehouseCode,
                s.CardCode,
                s.CardName,
                s.RouteCustomerCode,
                s.RouteCustomerName,
                s.PaymentMethod,
                s.TotalAmount,
                s.VatAmount,
                s.Currency,
                s.SapDocEntry,
                s.SapDocNum,
                s.ConsolidationStatus,
                s.FiscalizationStatus,
                s.FiscalizationRequiresReconciliation,
                s.FiscalError,
                s.FiscalReceiptNumber,
                s.ReceiptGlobalNo,
                s.FiscalVerificationCode,
                s.FiscalQRCode,
                s.FiscalDayNo,
                s.FiscalDeviceNumber,
                s.ReceiptIngestStatus,
                s.PostingAttempts,
                s.LastPostingError
            })
            .ToListAsync(cancellationToken);

        var records = new List<VanSalesInvoiceRecord>(reservations.Count + offline.Count);

        // Online sales and converted orders.
        var needLedgerLookup = new List<(int Index, InvoiceDto Probe)>();

        foreach (var r in reservations)
        {
            receiptByReference.TryGetValue(r.ExternalReferenceId, out var receipt);
            queueByReservation.TryGetValue(r.ReservationId, out var queue);

            var confirmed = r.Status == ReservationStatus.Confirmed;
            var hasReceiptRow = receipt is not null
                && (receipt.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success
                    || receipt.RequiresReconciliation);
            var liveQueueEntry = queue is not null && queue.Status != InvoiceQueueStatus.Cancelled;

            // Not a sale: a basket held for one that was refused, abandoned or expired.
            if (!confirmed && !hasReceiptRow && !liveQueueEntry)
            {
                continue;
            }

            var sapDocEntry = r.SAPDocEntry ?? receipt?.SapDocEntry ?? ParseDocEntry(queue?.SapDocEntry);
            var sapDocNum = r.SAPDocNum ?? receipt?.SapDocNum ?? queue?.SapDocNum;

            var signed = receipt?.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success;
            var queueSigned = !string.IsNullOrWhiteSpace(queue?.FiscalReceiptNumber);

            var failure = receipt?.RequiresReconciliation == true
                ? receipt.FiscalError ?? "The fiscal device could not confirm whether this sale was signed."
                : queue?.Status == InvoiceQueueStatus.RequiresReview
                    ? queue.LastError ?? "Parked for review."
                    : queue?.Status == InvoiceQueueStatus.Failed
                        ? queue.LastError
                        : sapDocNum is null && !string.IsNullOrWhiteSpace(receipt?.LastPostingError)
                            ? receipt!.LastPostingError
                            : null;

            var rep = VanSalesFacts.TryResolveRep(r.CreatedBy, out var repId) ? repId : (Guid?)null;

            var row = new VanSalesInvoiceRow(
                Reference: r.ExternalReferenceId,
                Channel: "Online",
                TradingDate: VanSalesFacts.TradingDayOf(r.CreatedAt),
                CreatedAtUtc: r.CreatedAt,
                RepUserId: rep,
                RepName: null,
                WarehouseCode: r.WarehouseCode,
                CustomerCode: r.RouteCustomerCode ?? r.CardCode,
                CustomerName: r.RouteCustomerName ?? r.CardName,
                PaymentMethod: r.PaymentMethod,
                Amount: receipt is not null && signed ? receipt.TotalAmount : r.TotalValue,
                VatAmount: receipt is not null && signed ? receipt.VatAmount : null,
                AmountIncludesVat: receipt is not null && signed,
                Currency: string.IsNullOrWhiteSpace(r.Currency) ? "USD" : r.Currency,
                SapDocEntry: sapDocEntry,
                SapDocNum: sapDocNum,
                FiscalReceiptNumber: signed ? receipt!.FiscalReceiptNumber : queue?.FiscalReceiptNumber,
                FiscalVerificationCode: signed ? receipt!.FiscalVerificationCode : null,
                FiscalDay: signed ? receipt!.FiscalDayNo : null,
                FiscalDeviceSerial: signed ? receipt!.FiscalDeviceNumber : null,
                State: VanSalesDocumentStates.Decide(signed || queueSigned, sapDocNum is not null, failure is not null),
                Problem: failure,
                SaleNumber: receipt is null ? null : DesktopSaleNumber.Format(receipt.Id));

            records.Add(new VanSalesInvoiceRecord(
                row,
                r.ReservationId,
                DesktopSaleId: null,
                FiscalQrCode: signed ? receipt!.FiscalQrCode : null,
                PostingAttempts: receipt?.PostingAttempts ?? 0,
                QueueStatus: queue?.Status.ToString(),
                Sale: receipt is null
                    ? null
                    : new VanSalesInvoiceSaleFacts(
                        SaleSourceSystems.VanSalesOnline,
                        receipt.ConsolidationStatus,
                        receipt.FiscalizationStatus,
                        receipt.RequiresReconciliation,
                        receipt.CreatedAtUtc)));

            if (!signed && !queueSigned && sapDocNum is > 0)
            {
                needLedgerLookup.Add((records.Count - 1, new InvoiceDto { DocNum = sapDocNum.Value }));
            }
        }

        // Offline sales.
        foreach (var s in offline)
        {
            var signed = s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success;

            var failure = s.FiscalizationRequiresReconciliation
                ? s.FiscalError ?? "The fiscal device could not confirm whether this sale was signed."
                : s.ReceiptIngestStatus is DesktopSaleReceiptIngestStatus.ChainBroken
                    or DesktopSaleReceiptIngestStatus.Unsignable
                    ? "The handset's signed receipt cannot be submitted to ZIMRA."
                    : s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Failed
                        ? s.FiscalError ?? "Not fiscalised."
                        : s.SapDocNum is null && !string.IsNullOrWhiteSpace(s.LastPostingError)
                            ? s.LastPostingError
                            : null;

            var rep = VanSalesFacts.TryResolveRep(s.CreatedBy, out var repId) ? repId : (Guid?)null;

            var row = new VanSalesInvoiceRow(
                Reference: s.ExternalReferenceId,
                Channel: "Offline",
                TradingDate: s.DocDate.Date,
                CreatedAtUtc: s.CreatedAt,
                RepUserId: rep,
                RepName: null,
                WarehouseCode: s.WarehouseCode,
                CustomerCode: s.RouteCustomerCode ?? s.CardCode,
                CustomerName: s.RouteCustomerName ?? s.CardName,
                PaymentMethod: s.PaymentMethod,
                Amount: s.TotalAmount,
                VatAmount: s.VatAmount,
                AmountIncludesVat: true,
                Currency: string.IsNullOrWhiteSpace(s.Currency) ? "USD" : s.Currency,
                SapDocEntry: s.SapDocEntry,
                SapDocNum: s.SapDocNum,
                FiscalReceiptNumber: s.FiscalReceiptNumber ?? s.ReceiptGlobalNo?.ToString(),
                FiscalVerificationCode: s.FiscalVerificationCode,
                FiscalDay: s.FiscalDayNo,
                FiscalDeviceSerial: s.FiscalDeviceNumber,
                State: VanSalesDocumentStates.Decide(signed, s.SapDocNum is not null, failure is not null),
                Problem: failure,
                SaleNumber: DesktopSaleNumber.Format(s.Id));

            records.Add(new VanSalesInvoiceRecord(
                row,
                null,
                s.Id,
                s.FiscalQRCode,
                s.PostingAttempts,
                QueueStatus: null,
                Sale: new VanSalesInvoiceSaleFacts(
                    SaleSourceSystems.VanSales,
                    s.ConsolidationStatus,
                    s.FiscalizationStatus,
                    s.FiscalizationRequiresReconciliation,
                    s.CreatedAt)));
        }

        if (needLedgerLookup.Count > 0)
        {
            var probes = needLedgerLookup.Select(n => n.Probe).ToList();
            await FiscalDocumentStatusProjector.EnrichInvoicesAsync(db, probes, cancellationToken);

            foreach (var (index, probe) in needLedgerLookup)
            {
                if (probe.IsFiscalized != true)
                {
                    continue;
                }

                var record = records[index];
                records[index] = record with
                {
                    Row = record.Row with
                    {
                        FiscalReceiptNumber = probe.FiscalReceiptGlobalNo?.ToString(),
                        FiscalVerificationCode = probe.FiscalVerificationCode,
                        FiscalDay = probe.FiscalDay,
                        FiscalDeviceSerial = probe.FiscalDeviceId,
                        State = VanSalesDocumentStates.Decide(true, true, record.Row.Problem is not null)
                    },
                    FiscalQrCode = probe.FiscalQrCode
                };
            }
        }

        return await NameRepsAsync(db, records, cancellationToken);
    }

    private static async Task<List<VanSalesInvoiceRecord>> NameRepsAsync(
        ApplicationDbContext db,
        List<VanSalesInvoiceRecord> records,
        CancellationToken cancellationToken)
    {
        var repIds = records
            .Select(r => r.Row.RepUserId)
            .OfType<Guid>()
            .Distinct()
            .ToList();

        if (repIds.Count == 0)
        {
            return records;
        }

        var names = await db.Users
            .AsNoTracking()
            .Where(u => repIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Username })
            .ToListAsync(cancellationToken);

        var nameById = names.ToDictionary(
            u => u.Id,
            u => string.Join(" ", new[] { u.FirstName, u.LastName }.Where(p => !string.IsNullOrWhiteSpace(p)))
                 is { Length: > 0 } full
                ? full
                : u.Username);

        return records
            .Select(r => r.Row.RepUserId is { } id && nameById.TryGetValue(id, out var name)
                ? r with { Row = r.Row with { RepName = name } }
                : r)
            .ToList();
    }

    private static int? ParseDocEntry(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    private sealed record ReceiptRow(
        int Id,
        string Reference,
        DesktopSaleFiscalizationStatus FiscalizationStatus,
        bool RequiresReconciliation,
        string? FiscalError,
        string? FiscalReceiptNumber,
        string? FiscalVerificationCode,
        string? FiscalQrCode,
        string? FiscalDayNo,
        string? FiscalDeviceNumber,
        decimal TotalAmount,
        decimal VatAmount,
        int? SapDocEntry,
        int? SapDocNum,
        int PostingAttempts,
        string? LastPostingError,
        DesktopSaleConsolidationStatus ConsolidationStatus,
        DateTime CreatedAtUtc);
}
