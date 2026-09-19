using ShopInventory.Models.Entities;

namespace ShopInventory.Data;

/// <summary>
/// The G/L accounts each partner's receipts used before the daily payment took over on 14 September 2026.
/// </summary>
/// <remarks>
/// Read from production SAP on 2026-09-19, covering 1,492 customer receipts dated 1 August to 13 September.
/// CIS006's accounts were given by finance. Where the history shows no electronic receipt for a partner,
/// its electronic account is 701100 (CABS) and the row is flagged for review. Every van put cash and
/// transfers in one account, so a van's two accounts are the same.
/// </remarks>
public static class IncomingPaymentGlMappingSeed
{
    private const string FactoryCash = "700300";
    private const string GranitesideCash = "700610";
    private const string Cabs = "701100";
    private const string Fbc = "701500";

    private static readonly DateTime SeededAt = new(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);

    public static readonly IncomingPaymentGlMappingEntity[] Rows =
    [
        Shop("CIS006", "Factory Kefalos shop POS  USD", FactoryCash, Cabs),
        Shop("COR007", "Graniteside Kefalos Shop USD POS", GranitesideCash, Cabs),
        Shop("COR006", "Cortina Graniteside Vending", GranitesideCash, Cabs, needsReview: true),
        Shop("COR011", "Bulawayo Kefalos Shop USD POS", Fbc, Cabs),
        Shop("COR008", "Cortina Bulawayo Vending", Fbc, Cabs, needsReview: true),
        Shop("MAC009", "Machipisa Kefalos Shop USD POS", Fbc, Cabs),
        Shop("MAC006", "Cortina Machipisa Vending", Fbc, Cabs, needsReview: true),
        Shop("MAC011", "Cortina Vending Special Events", Fbc, Cabs, needsReview: true),

        Van("VAN008", "Van Sales West 2", GranitesideCash),
        Van("VAN009", "Van Sales West 1", GranitesideCash),
        Van("VAN010", "Van Sales CBD", GranitesideCash),
        Van("VAN013", "Van Sales East", GranitesideCash),
        Van("VAN014", "Van Sales Up Country 1", GranitesideCash),
        Van("VAN015", "Van Sales Up Country 2", GranitesideCash),
        Van("VAN016", "Van Sales Up Country 3", GranitesideCash),
        Van("VAN017", "Van Sales Up Country 4", GranitesideCash),
        Van("VAN018", "Van Sales Bulawayo - Local", Fbc),
        Van("VAN019", "Van Sales Up Country 2 - Bulawayo", Fbc)
    ];

    private static IncomingPaymentGlMappingEntity Shop(
        string cardCode, string cardName, string cash, string electronic, bool needsReview = false) =>
        new()
        {
            CardCode = cardCode,
            CardName = cardName,
            CashAccount = cash,
            ElectronicAccount = electronic,
            Run = DailyPaymentRun.Shops,
            NeedsReview = needsReview,
            IsActive = true,
            UpdatedAtUtc = SeededAt,
            UpdatedBy = "seed"
        };

    private static IncomingPaymentGlMappingEntity Van(string cardCode, string cardName, string account) =>
        new()
        {
            CardCode = cardCode,
            CardName = cardName,
            CashAccount = account,
            ElectronicAccount = account,
            Run = DailyPaymentRun.Vans,
            IsActive = true,
            UpdatedAtUtc = SeededAt,
            UpdatedBy = "seed"
        };
}
