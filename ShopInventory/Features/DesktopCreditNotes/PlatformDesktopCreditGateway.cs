using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Features.DesktopCreditNotes;

/// <summary>
/// Credits a till, vending or van sale whose receipt the fiscalisation platform filed.
/// </summary>
/// <remarks>
/// <para>
/// The platform's lookup answers with a receipt's header — device, fiscal day, global number, FDMS
/// receipt id, currency and total — and not its lines. The lines are rebuilt from the sale through the
/// mapping that filed them (<see cref="DesktopSaleFiscaliser.BuildInvoice"/> and
/// <see cref="FiscalizationService.BuildPreSapLinesAsync"/>), and proved before they are offered: the
/// platform computes a tax-inclusive receipt's total as the sum of its rounded line totals, so rebuilt
/// lines that come to the archived total to the cent are the lines it filed. Lines that come to anything
/// else are refused rather than credited, because a credit carrying a price or a tax the original did not
/// is refused by ZIMRA after the fact.
/// </para>
/// <para>
/// The platform holds the authority on how much of the receipt is left to credit: it refuses a credit
/// past the original's total less every credit it has filed against it, SAP credit memos included. So
/// nothing filed elsewhere is subtracted here, and a credit that would overrun comes back as a refusal
/// with nothing filed.
/// </para>
/// </remarks>
public sealed class PlatformDesktopCreditGateway(ApplicationDbContext db, FiscalizationService platform,
    IFiscalisationApiClient client, IOptions<FiscalisationSettings> settings, IOptions<TaxSettings> tax)
    : IDesktopCreditFiscalGateway
{
    public async Task<DesktopCreditSource> ReadOriginalAsync(DesktopSaleEntity sale, CancellationToken ct) =>
        await TryReadOriginalAsync(sale, ct) ?? throw new InvalidOperationException(
            "The fiscalisation platform holds no receipt for this sale, so it cannot credit it.");

    /// <summary>The sale's receipt on the platform, or null when the platform says plainly it holds none.</summary>
    /// <remarks>Throws when the platform cannot be asked: that is not an answer either way.</remarks>
    public async Task<DesktopCreditSource?> TryReadOriginalAsync(DesktopSaleEntity sale, CancellationToken ct)
    {
        var number = settings.Value.BuildPreSapInvoiceNo(sale.ExternalReferenceId!);
        CheckFiscalisedReceiptApiResponse check;
        try
        {
            // Device 0 searches every device: the filing may have failed over.
            check = await client.CheckReceiptAsync(0, number, ReceiptType.FiscalInvoice, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The fiscalisation platform could not be asked for this sale's receipt, so the credit cannot be prepared. {ex.Message}", ex);
        }
        if (!check.IsFiscalised) return null;

        var matches = check.Matches.Where(m => m.InvoiceNo == number).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException(matches.Count == 0
                ? $"The fiscalisation platform reports {number} as filed but returned no receipt under that number. The credit cannot be prepared."
                : $"The fiscalisation platform holds {matches.Count} receipts under {number}. Reconcile them before crediting.");
        var original = matches[0];
        if (original.DeviceId <= 0 || original.FiscalDayNo <= 0 || original.ReceiptGlobalNo <= 0 || original.ReceiptTotal <= 0)
            throw new InvalidOperationException($"The fiscalisation platform's record of {number} has no device, day, receipt number or total. The credit cannot be prepared.");
        if (!string.IsNullOrWhiteSpace(sale.FiscalReceiptNumber) &&
            (!long.TryParse(sale.FiscalReceiptNumber, out var recorded) || recorded != original.ReceiptGlobalNo))
            throw new InvalidOperationException($"The original sale's recorded receipt number ({sale.FiscalReceiptNumber}) does not match the platform's receipt {original.ReceiptGlobalNo}. Reconcile the original receipt first.");

        // The sale is read without its lines.
        var saleLines = await db.DesktopSaleLines.AsNoTracking().Where(l => l.SaleId == sale.Id).ToListAsync(ct);
        var filed = await platform.BuildPreSapLinesAsync(DesktopSaleFiscaliser.BuildInvoice(sale, saleLines, tax.Value), ct);
        var rebuilt = filed.Sum(l => DesktopCreditPlanner.Round(l.Price * l.Quantity));
        if (rebuilt != original.ReceiptTotal)
            throw new InvalidOperationException(
                $"This sale's lines come to {Money(rebuilt)} as they would be filed today, but the platform's receipt "
                + $"{original.ReceiptGlobalNo} is for {Money(original.ReceiptTotal)}, so the lines it was filed with "
                + "cannot be reproduced. Raise the credit note in SAP.");

        var lines = new List<DesktopCreditLine>();
        var excluded = new List<string>();
        for (var index = 0; index < filed.Count; index++)
        {
            // Numbered by position, as the platform numbers them and as a credit finds its sale line.
            // See DesktopSaleLineOrder.
            var l = filed[index];
            var lineNo = index + 1;
            var name = string.IsNullOrWhiteSpace(l.Name) ? $"Line {lineNo}" : l.Name;
            // A credit line must carry a negative price, so a line sold for nothing has nothing to reverse.
            if (l.Price <= 0) { excluded.Add($"line {lineNo} ({name}) carries no value"); continue; }
            lines.Add(new DesktopCreditLine(lineNo, name, l.Quantity, l.Price, l.TaxId, l.TaxPercent, l.TaxCode, l.HsCode));
        }
        if (lines.Count == 0)
            throw new InvalidOperationException(
                $"None of the original receipt's {filed.Count} lines can be credited: {string.Join("; ", excluded)}.");

        return new DesktopCreditSource(number,
            string.IsNullOrWhiteSpace(original.ReceiptCurrency) ? sale.Currency : original.ReceiptCurrency,
            original.ReceiptTotal, original.DeviceId, original.FiscalDayNo, original.ReceiptGlobalNo,
            original.ReceiptId > 0 ? original.ReceiptId : null, lines,
            ExcludedLines: excluded.Count == 0 ? null : excluded, OnPlatform: true);
    }

    public async Task<FiscalizationResult?> FindAsync(DesktopCreditPlan plan, CancellationToken ct)
    {
        var number = plan.Receipt.InvoiceNo!;
        var check = await client.CheckReceiptAsync(0, number, ReceiptType.CreditNote, ct);
        if (!check.IsFiscalised) return null;
        var receipt = check.Matches.SingleOrDefault(m => m.InvoiceNo == number);
        if (receipt is null || Math.Abs(receipt.ReceiptTotal) != plan.Amount ||
            !string.Equals(receipt.ReceiptCurrency, plan.Source.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The saved credit-note lookup returned a different fiscal document.");
        return await platform.DescribeArchivedAsync(receipt,
            "The credit note's existing receipt on the fiscalisation platform was confirmed.", ct);
    }

    /// <remarks>
    /// Nothing to check before submitting. The platform's own preflight is not asked: it never opens a
    /// fiscal day or fails over to another device, both of which a submission does, so it refuses
    /// credits a submission would file. A refusal from the submission itself files nothing and releases
    /// the credit, so it costs no more than a preflight refusal would.
    /// </remarks>
    public Task<string?> PreflightAsync(DesktopCreditPlan plan, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<FiscalizationResult> SubmitAsync(DesktopCreditPlan plan, CancellationToken ct) =>
        platform.SubmitDesktopCreditAsync(BuildRequest(plan, settings.Value), ct);

    /// <summary>The plan's receipt as the platform takes it.</summary>
    /// <remarks>
    /// Three of the plan's fields are not sent as saved. The device is this deployment's pin, as for every
    /// other filing (see <see cref="FiscalisationSettings.DefaultDeviceId"/>); the credit's link to its
    /// original names the original's device. The date is left for the platform to assign under its device
    /// lock: a plan saves the moment it was made, in UTC, and a retry would send a date behind the
    /// device's last receipt. The user is left off because the plan carries an id and no full name, and
    /// the platform refuses one without the other.
    /// </remarks>
    internal static SubmitReceiptApiRequest BuildRequest(DesktopCreditPlan plan, FiscalisationSettings settings)
    {
        var receipt = plan.Receipt;
        return new SubmitReceiptApiRequest
        {
            DeviceId = settings.DefaultDeviceId, InvoiceNo = receipt.InvoiceNo, ReceiptType = ReceiptType.CreditNote,
            Currency = receipt.Currency, ReceiptDate = null, TaxInclusive = receipt.TaxInclusive,
            PaymentType = receipt.PaymentType, PaymentAmount = receipt.PaymentAmount, Lines = receipt.Lines,
            Buyer = receipt.Buyer, CreditDebitNote = receipt.CreditDebitNote, ReceiptNotes = receipt.ReceiptNotes,
            ReceiptPrintForm = receipt.ReceiptPrintForm,
            // A plan saved before origins were sent carries neither; the platform then shows the API key.
            SourceChannel = receipt.SourceChannel, SourceLocation = receipt.SourceLocation
        };
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
