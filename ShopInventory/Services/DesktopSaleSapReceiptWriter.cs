using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Sales;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Services;

/// <summary>
/// Gives the SAP invoice of a sale posted before its receipt the receipt, once the sale is signed.
/// </summary>
/// <remarks>
/// <para>
/// An ordinary till or vending sale is signed first and posted after, so its invoice carries the fiscal
/// code and QR URL UDFs from the POST and its remarks quote the receipt. An Admin's post during a fiscal
/// outage (<see cref="DesktopSaleEntity.PostedBeforeReceiptAt"/>) reaches SAP with neither, and remarks
/// that say the receipt is pending. Nothing went back afterwards: the platform fills the UDFs only on
/// documents it fiscalises from SAP, and this sale is signed under its own reference. So the invoice
/// printed without a QR code and went on saying its receipt was pending after it had one.
/// </para>
/// <para>
/// Runs from <see cref="DesktopSalePostingJob"/> after each posting pass, so it shares that job's key and
/// SAP gate and needs no schedule of its own. It is never part of signing: the receipt is already filed
/// by the time this runs, so a SAP refusal is recorded on the sale and retried, and the sale stays
/// fiscalised whatever happens here. Sales signed before this existed are picked up the same way.
/// </para>
/// <para>
/// It reads the invoice before writing to it. A DocNum that is not the sale's means the DocEntry points
/// somewhere else, and a fiscal code already on the invoice that is not this receipt's means somebody
/// fiscalised the invoice in SAP as well, which is two receipts for one sale. Either is left for a person
/// rather than overwritten.
/// </para>
/// </remarks>
public sealed class DesktopSaleSapReceiptWriter(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    SapCircuitBreakerState circuitState,
    IOptions<FiscalisationSettings> fiscalisationSettings,
    IOptions<DesktopSalePostingSettings> settings,
    ILogger<DesktopSaleSapReceiptWriter> logger)
{
    // The sizes U_Fiscal_Code and U_Fiscal_Url are defined with in production, as on the invoice POST.
    private const int FiscalCodeSize = 100;
    private const int FiscalUrlSize = 200;

    public async Task<DesktopSaleSapReceiptRunResult> WriteOwedReceiptsAsync(CancellationToken cancellationToken = default)
    {
        var result = new DesktopSaleSapReceiptRunResult();

        if (circuitState.ShouldHoldBackWork(out var holdBackReason))
        {
            logger.LogDebug("Skipping SAP receipt write-back: {Reason}.", holdBackReason);
            return result;
        }

        var options = settings.Value;

        // No date window. These are rare, and a sale that was signed days after it was posted is exactly
        // the one this exists for.
        var owed = await context.DesktopSales
            .Where(s => s.PostedBeforeReceiptAt != null &&
                        s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success &&
                        s.SapDocEntry != null &&
                        s.SapReceiptWrittenAt == null &&
                        s.SapReceiptWriteAttempts < options.MaxPostingAttempts)
            .OrderBy(s => s.Id)
            .Take(options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var sale in owed)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var refusal = await WriteAsync(sale, cancellationToken);

                if (refusal is null)
                {
                    sale.SapReceiptWrittenAt = DateTime.UtcNow;
                    sale.SapReceiptWriteError = null;
                    result.Written++;
                }
                else
                {
                    Refuse(sale, refusal);
                    result.Failed++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "Could not give SAP invoice {DocNum} the receipt of sale {ExternalReference}.",
                    sale.SapDocNum, sale.ExternalReferenceId);
                Refuse(sale, ex.Message);
                result.Failed++;
            }

            await context.SaveChangesAsync(CancellationToken.None);
        }

        return result;
    }

    /// <returns>Null when the invoice now carries the receipt; otherwise why it was left alone.</returns>
    private async Task<string?> WriteAsync(DesktopSaleEntity sale, CancellationToken cancellationToken)
    {
        var udf = fiscalisationSettings.Value.Udf;
        var codeField = udf.FiscalCodeField?.Trim();
        var urlField = udf.FiscalUrlField?.Trim();

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
        Want(wanted, codeField,
            string.IsNullOrWhiteSpace(sale.FiscalVerificationCode)
                ? null
                : FiscalReceiptQrComposer.FormatVerificationCode(sale.FiscalVerificationCode),
            FiscalCodeSize);
        Want(wanted, urlField, sale.FiscalQRCode?.Trim(), FiscalUrlSize);

        var read = new List<string> { "DocNum", "Comments" };
        read.AddRange(wanted.Keys);

        var invoice = await sapClient.GetInvoiceFieldsAsync(sale.SapDocEntry!.Value, read, cancellationToken);

        if (invoice is null)
        {
            return $"SAP has no invoice with DocEntry {sale.SapDocEntry}.";
        }

        if (invoice.GetValueOrDefault("DocNum") != sale.SapDocNum?.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            return $"SAP invoice DocEntry {sale.SapDocEntry} is DocNum {invoice.GetValueOrDefault("DocNum") ?? "(none)"}, "
                + $"but the sale was posted as {sale.SapDocNum}. Left alone.";
        }

        var patch = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (field, value) in wanted)
        {
            var current = invoice.GetValueOrDefault(field);

            if (current is null)
            {
                patch[field] = value;
            }
            else if (!string.Equals(current.Trim(), value, StringComparison.OrdinalIgnoreCase))
            {
                return $"Invoice {sale.SapDocNum} already holds {field} '{current}', which is not this sale's receipt. "
                    + "It may have been fiscalised in SAP as well; check it before changing anything.";
            }
        }

        var comments = invoice.GetValueOrDefault("Comments");
        var withReceipt = DesktopSaleInvoiceRemarks.WithReceipt(comments, sale);

        if (withReceipt is not null)
        {
            patch["Comments"] = withReceipt.Length <= DesktopSaleInvoiceRemarks.MaxLength
                ? withReceipt
                : DesktopSaleInvoiceRemarks.Build(
                    sale, await DesktopSaleInvoiceRemarks.ResolveNamesAsync(context, sale, logger, cancellationToken));
        }

        if (patch.Count > 0)
        {
            await sapClient.UpdateInvoiceFieldsAsync(sale.SapDocEntry.Value, patch, cancellationToken);

            logger.LogInformation(
                "Gave SAP invoice {DocNum} the receipt of sale {ExternalReference}: {Fields}.",
                sale.SapDocNum, sale.ExternalReferenceId, string.Join(", ", patch.Keys));
        }

        return null;
    }

    /// <remarks>
    /// A value too long for its field is left out rather than cut, as on the invoice POST: a cut URL is a
    /// QR code that resolves nowhere.
    /// </remarks>
    private static void Want(Dictionary<string, string> wanted, string? field, string? value, int size)
    {
        if (!string.IsNullOrEmpty(field) && !string.IsNullOrEmpty(value) && value.Length <= size)
        {
            wanted[field] = value;
        }
    }

    private void Refuse(DesktopSaleEntity sale, string reason)
    {
        sale.SapReceiptWriteAttempts++;
        sale.SapReceiptWriteError = reason.Length > 1000 ? reason[..1000] : reason;

        if (sale.SapReceiptWriteAttempts >= settings.Value.MaxPostingAttempts)
        {
            logger.LogError(
                "Gave up giving SAP invoice {DocNum} the receipt of sale {ExternalReference} after {Attempts} attempts: {Reason}",
                sale.SapDocNum, sale.ExternalReferenceId, sale.SapReceiptWriteAttempts, reason);
        }
    }
}

public sealed class DesktopSaleSapReceiptRunResult
{
    public int Written { get; set; }
    public int Failed { get; set; }
}
