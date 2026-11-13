using ShopInventory.Common.Sales;

namespace ShopInventory.Features.VendingCashUps;

/// <summary>
/// The variance arithmetic of a cash-up, kept in one place so the API, the tests and the reconciliation
/// read agree on it.
/// </summary>
/// <remarks>
/// <para>
/// Cash: <c>variance = counted − (opening float + expected cash sales)</c>, per currency. The count
/// includes the float, so the float is taken back out before the drawer is held against the sales.
/// Negative is a shortfall, positive is cash over.
/// </para>
/// <para>
/// A non-cash tender: <c>variance = declared − expected</c>, per currency and tender.
/// </para>
/// <para>
/// Everything is rounded to the cent before it is compared, so a sum of many prices that lands a
/// hair off a whole cent is not reported as a variance nobody can find.
/// </para>
/// </remarks>
public static class VendingCashUpMath
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>What the drawer should hold at the end of the day: the float plus the cash sales.</summary>
    public static decimal ExpectedInDrawer(decimal openingFloat, decimal expectedCash) =>
        Round(openingFloat + expectedCash);

    public static decimal CashVariance(decimal countedCash, decimal openingFloat, decimal expectedCash) =>
        Round(countedCash - ExpectedInDrawer(openingFloat, expectedCash));

    public static decimal TenderVariance(decimal declared, decimal expected) => Round(declared - expected);

    /// <summary>The takings the count holds: what should go to the bank, the float being kept back.</summary>
    public static decimal ToBank(decimal countedCash, decimal openingFloat) =>
        Math.Max(0m, Round(countedCash - openingFloat));

    /// <summary>The sum of a currency's denomination lines.</summary>
    public static decimal DenominationTotal(IEnumerable<(decimal Value, int Quantity)> lines) =>
        Round(lines.Sum(line => line.Value * line.Quantity));

    /// <summary>Whether every currency and tender agrees with the sales to the cent.</summary>
    public static bool IsBalanced(IEnumerable<decimal> variances) => variances.All(variance => Round(variance) == 0m);

    /// <summary>
    /// One spelling per currency. The till stores ZiG as <c>ZWG</c> (its ISO code) and the portal as
    /// <c>ZIG</c>; both are one currency and must land on one line, or a ZiG count would sit beside a
    /// "ZWG" expectation with the whole day as its variance.
    /// </summary>
    public static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return "USD";
        }

        var upper = currency.Trim().ToUpperInvariant();
        return upper is "ZWG" or "ZIG" ? "ZIG" : upper;
    }

    /// <summary>
    /// The tender a sale is reconciled under. Vending sells for cash only and its tills have sent no
    /// tender at all on some builds, so a vending sale with none is cash: the cashier took notes for it.
    /// </summary>
    public static string ReconciledTender(string? paymentMethod)
    {
        if (string.IsNullOrWhiteSpace(paymentMethod))
        {
            return TenderTypes.Cash;
        }

        return TenderTypes.ReportingName(paymentMethod);
    }
}
