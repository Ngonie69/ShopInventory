using System.Globalization;
using System.Text.Json;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>What ZIMRA already holds against an original receipt, filed by something other than this dialog.</summary>
/// <param name="Amount">The credit receipts' totals, as positive values.</param>
/// <param name="Credits">One entry per credit receipt, naming the SAP credit memo it was filed under.</param>
public sealed record DesktopCreditExternalHistory(decimal Amount, List<string> Credits)
{
    public static readonly DesktopCreditExternalHistory None = new(0m, []);
}

public interface IDesktopCreditExternalCredits
{
    /// <summary>
    /// The credit receipts ZIMRA holds against <paramref name="receiptGlobalNo"/> that were filed under a
    /// SAP credit memo's number rather than through this dialog.
    /// </summary>
    Task<DesktopCreditExternalHistory> FindAsync(DesktopSaleEntity sale, int deviceId, int receiptGlobalNo,
        CancellationToken ct);
}

/// <summary>
/// Reads, from the device, the credits already filed against a sale's receipt by the other routes onto
/// it — the SAP credit-note screen, the InvoiceFiscalisation tool and the vendor's SAP add-on.
/// </summary>
/// <remarks>
/// <para>
/// ZIMRA refuses a credit that takes a receipt's credits past its total, and it counts every credit,
/// not only the ones this dialog saved. A second one filed here after a SAP credit memo was already
/// fiscalised is an over-credit the device may accept and ZIMRA then rejects.
/// </para>
/// <para>
/// REVMax cannot be asked "which credits reference this receipt"; <c>GetInvoice</c> answers one number
/// at a time. Every other route files a credit under its SAP credit memo's <c>DocNum</c>, so the memos
/// raised for the customer since the sale are the candidates, and each is kept only when the device
/// holds it as a credit note on our device whose <c>creditDebitNote</c> names this very receipt. A memo
/// number another device, or an unrelated invoice, happens to share is skipped for that reason.
/// </para>
/// <para>
/// <b>Unknown is not "none".</b> A SAP read or device lookup that fails refuses the credit rather than
/// letting it through unchecked. A sale with no SAP invoice at all — neither its own nor a consolidated
/// one — can have no memo against it, so it is not checked and a till can still credit while SAP is down.
/// A credit filed under a number that is not a SAP credit memo cannot be found this way.
/// </para>
/// </remarks>
public sealed class DesktopCreditExternalCredits(ISAPServiceLayerClient sap, IRevmaxClient client,
    ILogger<DesktopCreditExternalCredits> logger) : IDesktopCreditExternalCredits
{
    public async Task<DesktopCreditExternalHistory> FindAsync(DesktopSaleEntity sale, int deviceId, int receiptGlobalNo,
        CancellationToken ct)
    {
        if (sale.SapDocEntry is not > 0 && sale.ConsolidationStatus != DesktopSaleConsolidationStatus.Consolidated)
            return DesktopCreditExternalHistory.None;

        List<Models.SAPCreditNote> memos;
        try
        {
            memos = await sap.GetCreditNotesByCustomerAsync(sale.CardCode, sale.DocDate.Date.AddDays(-1),
                DateTime.UtcNow.Date.AddDays(1), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not read SAP credit memos for {CardCode} to check sale {Sale}'s receipt",
                sale.CardCode, sale.ExternalReferenceId);
            throw new InvalidOperationException($"SAP could not be asked which credit memos exist for {sale.CardCode}, so "
                + "it is not known whether ZIMRA already holds credits against this receipt. Try again.");
        }

        var device = deviceId.ToString(CultureInfo.InvariantCulture);
        var amount = 0m;
        var credits = new List<string>();
        var counted = new HashSet<long>();
        foreach (var memo in memos.Where(m => m.DocNum > 0).DistinctBy(m => m.DocNum).OrderBy(m => m.DocNum))
        {
            var number = memo.DocNum.ToString(CultureInfo.InvariantCulture);
            var filed = await LookUp(number, sale, ct);
            if (filed?.Data is not { } receipt || filed.DeviceID != device
                || !string.Equals(receipt.ReceiptType, "CreditNote", StringComparison.OrdinalIgnoreCase)
                || !(receipt.InvoiceNo == number || receipt.InvoiceNo == $"{device}-{number}"))
                continue;
            if (!References(receipt.CreditDebitNote, deviceId, receiptGlobalNo, out var readable))
            {
                if (readable) continue;
                throw new InvalidOperationException($"REVMax holds credit memo {number} as a credit note but its reference "
                    + "to the original receipt could not be read, so the receipt's remaining balance is not known.");
            }
            if (!counted.Add(receipt.ReceiptGlobalNo)) continue;
            var total = Math.Abs(receipt.ReceiptTotal);
            amount += total;
            credits.Add($"SAP credit memo {number} (receipt {receipt.ReceiptGlobalNo}, {receipt.ReceiptCurrency} "
                + $"{total.ToString("0.00", CultureInfo.InvariantCulture)})");
        }
        if (credits.Count > 0)
            logger.LogInformation("Receipt {GlobalNo} of sale {Sale} already carries {Count} credits worth {Amount}: {Credits}",
                receiptGlobalNo, sale.ExternalReferenceId, credits.Count, amount, credits);
        return new DesktopCreditExternalHistory(amount, credits);
    }

    /// <summary>The device's record under a memo number, or null when it holds none.</summary>
    private async Task<Models.Revmax.InvoiceResponse?> LookUp(string number, DesktopSaleEntity sale, CancellationToken ct)
    {
        Models.Revmax.InvoiceResponse? filed;
        try { filed = await client.GetInvoiceAsync(number, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "REVMax lookup of credit memo {Number} failed while checking sale {Sale}", number,
                sale.ExternalReferenceId);
            filed = null;
        }
        if (filed?.Success == true) return filed;
        if (filed?.Message?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true) return null;
        throw new InvalidOperationException($"REVMax could not say whether SAP credit memo {number} was fiscalised against "
            + "this receipt, so its remaining balance is not known. Try again.");
    }

    /// <summary>Whether a credit receipt's <c>creditDebitNote</c> names the given original receipt.</summary>
    /// <param name="readable">False when the reference is missing or not in a shape that can be read.</param>
    /// <remarks>
    /// The device passes FDMS's receipt through, where the reference is
    /// <c>{ receiptID, deviceID, receiptGlobalNo, fiscalDayNo }</c>. Matched on device and global number,
    /// the pair that identifies a receipt; names are read case-blind and numbers as numbers or strings.
    /// </remarks>
    internal static bool References(object? reference, int deviceId, int receiptGlobalNo, out bool readable)
    {
        readable = false;
        if (reference is not JsonElement { ValueKind: JsonValueKind.Object } element) return false;
        long? device = null, global = null;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals("deviceID", StringComparison.OrdinalIgnoreCase)) device = Number(property.Value);
            else if (property.Name.Equals("receiptGlobalNo", StringComparison.OrdinalIgnoreCase)) global = Number(property.Value);
        }
        if (device is null || global is null) return false;
        readable = true;
        return device == deviceId && global == receiptGlobalNo;
    }

    private static long? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
        _ => null
    };
}
