using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopCreditNotes;

public interface IDesktopCreditFiscalDays
{
    /// <summary>
    /// The fiscal day receipt <paramref name="receiptGlobalNo"/> (counter <paramref name="receiptCounter"/>
    /// within its day) on our device went into, when a receipt of that same day proves it; otherwise null.
    /// </summary>
    Task<int?> ResolveAsync(DesktopSaleEntity sale, long receiptGlobalNo, int receiptCounter, CancellationToken ct);
}

/// <summary>
/// Recovers a receipt's fiscal day from another receipt filed into the same day.
/// </summary>
/// <remarks>
/// <para>
/// Filing records the day only when the device can vouch for it at that moment (see
/// <c>RevmaxFiscalizationService.StampFiscalDayAsync</c>), so a sale filed while FDMS was behind, or one
/// whose receipt was adopted on a retry, carries a blank day and could never be credited. The day is not
/// guessed from REVMax's <c>FiscalDay</c> envelope, which is the device's day and not the receipt's:
/// van sale VAN005-INV-20260917-E58A14, filed 2026-09-17, read 535 there on the 18th.
/// </para>
/// <para>
/// <c>receiptGlobalNo</c> never resets and <c>receiptCounter</c> restarts at 1 each fiscal day, so both
/// step by one per receipt within a day and <c>global - counter</c> is the same for every receipt of that
/// day — and different for every other day that has receipts. FDMS holds 216877 (counter 985) and 216407
/// (counter 515) both on day 525: 215892 each. So a neighbouring sale whose day was vouched for when it
/// was filed, and whose receipt the device confirms as ours with the same <c>global - counter</c>, is on
/// the same day. Nothing short of that answers: a wrong reference is a misstatement ZIMRA flags, a
/// refusal is not.
/// </para>
/// </remarks>
public sealed class DesktopCreditFiscalDays(ApplicationDbContext db, IRevmaxClient client,
    RevmaxFiscalizationService fiscal, IOptions<RevmaxSettings> settings,
    ILogger<DesktopCreditFiscalDays> logger) : IDesktopCreditFiscalDays
{
    /// <summary>How far either side of the sale to look for neighbours. Days turn over daily.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromDays(4);

    /// <summary>Device lookups per side. The nearest stamped receipts are the likeliest to share the day.</summary>
    private const int PerSide = 3;

    public async Task<int?> ResolveAsync(DesktopSaleEntity sale, long receiptGlobalNo, int receiptCounter,
        CancellationToken ct)
    {
        if (receiptCounter <= 0 || receiptCounter > receiptGlobalNo)
            return null;
        var dayKey = receiptGlobalNo - receiptCounter;
        var device = settings.Value.DefaultRefDeviceId;
        var from = sale.CreatedAt - Window;
        var to = sale.CreatedAt + Window;
        var rows = await db.DesktopSales.AsNoTracking()
            .Where(s => s.Id != sale.Id && s.CreatedAt >= from && s.CreatedAt <= to
                && s.FiscalizationStatus == DesktopSaleFiscalizationStatus.Success
                && (s.FiscalDeviceId == null || s.FiscalDeviceId == device)
                && s.FiscalDayNo != null && s.FiscalReceiptNumber != null && s.ExternalReferenceId != null)
            .Select(s => new { s.ExternalReferenceId, s.FiscalReceiptNumber, s.FiscalDayNo })
            .ToListAsync(ct);

        var stamped = rows
            .Select(r => (Reference: r.ExternalReferenceId!,
                Global: long.TryParse(r.FiscalReceiptNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var g) ? g : 0,
                Day: int.TryParse(r.FiscalDayNo, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : 0))
            .Where(r => r.Global > 0 && r.Day > 0 && r.Global != receiptGlobalNo)
            .ToList();
        var candidates = stamped.Where(r => r.Global < receiptGlobalNo).OrderByDescending(r => r.Global).Take(PerSide)
            .Concat(stamped.Where(r => r.Global > receiptGlobalNo).OrderBy(r => r.Global).Take(PerSide))
            .OrderBy(r => Math.Abs(r.Global - receiptGlobalNo));

        foreach (var candidate in candidates)
        {
            if (await CounterOffsetAsync(candidate.Reference, candidate.Global, device, ct) != dayKey)
                continue;
            logger.LogInformation(
                "Sale {Sale}'s receipt {Receipt} has no recorded fiscal day; placed on day {Day} with receipt {Neighbour} "
                + "of sale {NeighbourSale}, which shares its global-minus-counter {DayKey}",
                sale.ExternalReferenceId, receiptGlobalNo, candidate.Day, candidate.Global, candidate.Reference, dayKey);
            return candidate.Day;
        }
        return null;
    }

    /// <summary>
    /// <c>global - counter</c> of the sale's receipt as the device holds it, or null unless the device holds
    /// it as a fiscal invoice on our device under the global number we recorded.
    /// </summary>
    private async Task<long?> CounterOffsetAsync(string reference, long global, int device, CancellationToken ct)
    {
        var number = fiscal.BuildPreSapInvoiceNo(reference);
        var found = await client.GetInvoiceAsync(number, ct);
        var id = device.ToString(CultureInfo.InvariantCulture);
        return found is { Success: true, Data: { ReceiptCounter: > 0 } data } && found.DeviceID == id
            && string.Equals(data.ReceiptType, "FiscalInvoice", StringComparison.OrdinalIgnoreCase)
            && (data.InvoiceNo == number || data.InvoiceNo == $"{id}-{number}")
            && data.ReceiptGlobalNo == global
                ? global - data.ReceiptCounter
                : null;
    }
}
