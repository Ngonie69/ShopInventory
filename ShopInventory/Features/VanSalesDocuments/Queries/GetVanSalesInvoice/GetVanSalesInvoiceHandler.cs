using System.Globalization;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoice;

public sealed class GetVanSalesInvoiceHandler(
    ApplicationDbContext db,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    IOptions<TaxSettings> tax,
    IFiscalisationApiClient fiscalClient,
    IFiscalDeviceConfigCache fiscalConfig,
    ILogger<GetVanSalesInvoiceHandler> logger)
    : IRequestHandler<GetVanSalesInvoiceQuery, ErrorOr<VanSalesInvoiceDetail>>
{
    /// <summary>How long the drawer waits on the platform for a ZIMRA link before opening without one.</summary>
    private static readonly TimeSpan ZimraLinkBudget = TimeSpan.FromSeconds(5);

    public async Task<ErrorOr<VanSalesInvoiceDetail>> Handle(
        GetVanSalesInvoiceQuery request,
        CancellationToken cancellationToken)
    {
        var reference = request.Reference?.Trim();

        if (string.IsNullOrEmpty(reference))
        {
            return Error.Validation("VanSalesDocuments.MissingReference", "A van order reference is required.");
        }

        // The period is ignored when a reference is given; the dates only have to be valid.
        var records = await VanSalesInvoiceReader.LoadAsync(
            db, tax.Value, DateTime.UtcNow.Date, DateTime.UtcNow.Date, reference, cancellationToken);

        var record = records.FirstOrDefault();

        if (record is null)
        {
            return Error.NotFound(
                "VanSalesDocuments.InvoiceNotFound",
                $"No invoice from the van sales app has the reference {reference}.");
        }

        record = await WithZimraLinkAsync(record, cancellationToken);

        var lines = record.ReservationId is not null
            ? await db.StockReservationLines
                .AsNoTracking()
                .Where(l => l.Reservation.ReservationId == record.ReservationId)
                .OrderBy(l => l.LineNum)
                .Select(l => new VanSalesInvoiceLine(
                    l.LineNum,
                    l.ItemCode,
                    l.ItemDescription,
                    l.OriginalQuantity,
                    l.UoMCode,
                    l.UnitPrice,
                    l.DiscountPercent,
                    l.LineTotal,
                    l.TaxCode))
                .ToListAsync(cancellationToken)
            : await db.DesktopSaleLines
                .AsNoTracking()
                .Where(l => l.SaleId == record.DesktopSaleId)
                .OrderBy(l => l.LineNum)
                .Select(l => new VanSalesInvoiceLine(
                    l.LineNum,
                    l.ItemCode,
                    l.ItemDescription,
                    l.Quantity,
                    l.UoMCode,
                    l.UnitPrice,
                    l.DiscountPercent,
                    l.LineTotal,
                    l.TaxCode))
                .ToListAsync(cancellationToken);

        var (postRefusal, fiscaliseRefusal) = Refusals(
            record, fiscalisationSettings.Value.UsesPlatform, DateTime.UtcNow);

        return new VanSalesInvoiceDetail(
            record.Row,
            record.FiscalQrCode,
            record.PostingAttempts,
            record.QueueStatus,
            postRefusal,
            fiscaliseRefusal,
            lines,
            await LoadCreditsAsync(reference, record.Row.SapDocEntry, cancellationToken));
    }

    /// <summary>
    /// The invoice with the ZIMRA verification link its receipt was signed with, read back from the platform
    /// where nothing here stored it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A converted order keeps only its receipt number: its queue entry has no column for the QR. A sale whose
    /// receipt was adopted on a retry has none either, because the platform's adoption check is mapped without
    /// one. Both show "Receipt 1871" and nothing to verify it by. The platform's receipt check does return the
    /// device signature, and the link is composed from that exactly as the invoice status sync composes it.
    /// </para>
    /// <para>
    /// Asked under each number the receipt may have been filed under — the pre-SAP number of the queue entry
    /// and of the sale, then the SAP DocNum — and taken only from a match carrying the receipt number this
    /// invoice already shows, so a receipt filed under another number is never put beside it. Nothing is
    /// written back: this is a read, and the drawer opens without the link if the platform is slow or down.
    /// </para>
    /// </remarks>
    private async Task<VanSalesInvoiceRecord> WithZimraLinkAsync(
        VanSalesInvoiceRecord record,
        CancellationToken cancellationToken)
    {
        var settings = fiscalisationSettings.Value;
        var receiptNumber = record.Row.FiscalReceiptNumber?.Trim();

        if (!settings.UsesPlatform
            || !settings.Enabled
            || string.IsNullOrEmpty(receiptNumber)
            || !string.IsNullOrWhiteSpace(record.FiscalQrCode))
        {
            return record;
        }

        var invoiceNumbers = new[] { record.QueueReference, record.Row.Reference }
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => settings.BuildPreSapInvoiceNo(r!))
            .Append(record.Row.SapDocNum?.ToString(CultureInfo.InvariantCulture))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ZimraLinkBudget);

        try
        {
            foreach (var invoiceNo in invoiceNumbers)
            {
                var receipt = await FiscalReceiptLookup.TryLookupAsync(
                    fiscalClient, fiscalConfig, invoiceNo, ReceiptType.FiscalInvoice, logger, budget.Token);

                if (receipt is not { IsFiscalised: true, QrCode: { Length: > 0 } qrCode }
                    || receipt.ReceiptGlobalNo?.ToString(CultureInfo.InvariantCulture) != receiptNumber)
                {
                    continue;
                }

                return record with
                {
                    FiscalQrCode = qrCode,
                    Row = record.Row with
                    {
                        FiscalVerificationCode = record.Row.FiscalVerificationCode ?? receipt.VerificationCode,
                        FiscalDay = record.Row.FiscalDay ?? receipt.FiscalDay,
                        FiscalDeviceSerial = record.Row.FiscalDeviceSerial ?? receipt.DeviceSerialNumber
                    }
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "The platform did not answer for receipt {Receipt} of van invoice {Reference} within {Budget}; "
                + "the invoice is shown without its ZIMRA link",
                receiptNumber,
                record.Row.Reference,
                ZimraLinkBudget);
        }

        return record;
    }

    /// <summary>
    /// Why the invoice may not be posted to SAP, or fiscalised, on request — or null where it may.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same two rules the desktop sales console offers its buttons on and the two commands refuse
    /// from, asked of the sale row the invoice has: the receipt row of an online sale, the sale itself
    /// for an offline one. Asked with the invoice's own SAP number rather than the row's, because an
    /// online sale's number can sit on its reservation before the receipt row has it — and a sale SAP
    /// holds is not offered a post on the strength of a row that has not caught up.
    /// </para>
    /// <para>
    /// A converted order has no sale row for either command to find. It is signed and posted from its
    /// queue entry, and the refusal says where to send it instead of naming a button that would answer
    /// "not found".
    /// </para>
    /// </remarks>
    internal static (string? Post, string? Fiscalise) Refusals(
        VanSalesInvoiceRecord record,
        bool usesPlatform,
        DateTime nowUtc)
    {
        if (record.Sale is { } sale)
        {
            return (
                DesktopSalePostEligibility.Refusal(
                    sale.SourceSystem, sale.ConsolidationStatus, sale.FiscalizationStatus, record.Row.SapDocNum),
                DesktopSaleFiscalisationRetry.ManualRefusal(
                    sale.SourceSystem,
                    sale.FiscalizationStatus,
                    sale.RequiresReconciliation,
                    sale.CreatedAtUtc,
                    nowUtc,
                    usesPlatform));
        }

        var parked = string.Equals(
            record.QueueStatus, nameof(InvoiceQueueStatus.RequiresReview), StringComparison.Ordinal);

        var post = record.Row.SapDocNum is not null
            ? "This sale is already in SAP."
            : parked
                ? "This sale is posted from its invoice queue entry, which is parked for review. Put it back "
                  + "with Retry in the Exception Center and the queue posts it."
                : "This sale is waiting in the invoice queue, which posts it on its next run.";

        var fiscalise = !string.IsNullOrWhiteSpace(record.Row.FiscalReceiptNumber)
            ? "This sale is already fiscalised."
            : parked
                ? "This sale is signed from its invoice queue entry, which is parked for review. Put it back "
                  + "with Retry in the Exception Center and the queue asks the device before signing."
                : "This sale is waiting in the invoice queue, which signs it on its next run.";

        return (post, fiscalise);
    }

    /// <summary>
    /// The credits against this invoice: SAP memos whose lines name it as their base document, and till credits
    /// raised against its sale. Found the way the credit notes list finds them, so the two agree.
    /// </summary>
    private async Task<List<VanSalesInvoiceCredit>> LoadCreditsAsync(
        string reference,
        int? sapDocEntry,
        CancellationToken cancellationToken)
    {
        var credits = new List<VanSalesInvoiceCredit>();
        var memoEntries = new HashSet<int>();

        if (sapDocEntry is { } docEntry)
        {
            var memoLines = await db.SapCreditNoteSnapshots
                .AsNoTracking()
                .SelectMany(c => c.Lines)
                .Where(l => l.BaseType == VanSaleCreditNotes.InvoiceBaseType && l.BaseEntry == docEntry)
                .Select(l => new { l.CreditNoteDocEntry, l.CreditReason })
                .ToListAsync(cancellationToken);

            var entries = memoLines.Select(l => l.CreditNoteDocEntry).Distinct().ToList();

            var memos = entries.Count == 0
                ? []
                : await db.SapCreditNoteSnapshots
                    .AsNoTracking()
                    .Where(c => entries.Contains(c.SapDocEntry))
                    .Select(c => new { c.SapDocEntry, c.SapDocNum, c.DocDate, c.DocTotal, c.DocCurrency, c.Comments, c.IsCancelled })
                    .ToListAsync(cancellationToken);

            foreach (var memo in memos)
            {
                memoEntries.Add(memo.SapDocEntry);

                credits.Add(new VanSalesInvoiceCredit(
                    $"sap-{memo.SapDocEntry}",
                    "SAP",
                    memo.SapDocNum.ToString(),
                    memo.DocDate.Date,
                    memo.DocTotal,
                    string.IsNullOrWhiteSpace(memo.DocCurrency) ? "USD" : memo.DocCurrency,
                    memoLines.FirstOrDefault(l => l.CreditNoteDocEntry == memo.SapDocEntry
                                                  && !string.IsNullOrWhiteSpace(l.CreditReason))?.CreditReason
                        ?? memo.Comments,
                    memo.IsCancelled,
                    GivesBack: !memo.IsCancelled));
            }
        }

        var tillCredits = await db.DesktopCreditNotes
            .AsNoTracking()
            .Where(c => SaleSourceSystems.VanSaleSources.Contains(c.Sale.SourceSystem!)
                        && c.Sale.ExternalReferenceId == reference
                        && c.Status != DesktopCreditStatuses.Rejected)
            .Select(c => new { c.Id, c.Number, c.CreatedAtUtc, c.Amount, c.Currency, c.Reason, c.Status, c.SapDocEntry })
            .ToListAsync(cancellationToken);

        foreach (var credit in tillCredits.Where(c => c.SapDocEntry is null || !memoEntries.Contains(c.SapDocEntry.Value)))
        {
            credits.Add(new VanSalesInvoiceCredit(
                $"till-{credit.Id:N}",
                "Till",
                credit.Number,
                VanSalesFacts.TradingDayOf(credit.CreatedAtUtc),
                credit.Amount,
                string.IsNullOrWhiteSpace(credit.Currency) ? "USD" : credit.Currency,
                credit.Reason,
                IsCancelled: false,
                GivesBack: credit.Status == DesktopCreditStatuses.Fiscalised));
        }

        return credits
            .OrderBy(c => c.Date)
            .ThenBy(c => c.Number, StringComparer.Ordinal)
            .ToList();
    }
}
