namespace ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppReport;

/// <summary>One day's van and shop sales, as the WhatsApp message states them, and the message itself.</summary>
/// <remarks><list type="table">
/// <item><term>VanLines</term><description>Van takings in US dollars, VAT included, per row of the van table — the daily truck report's figures.</description></item>
/// <item><term>VanTotal</term><description>Every line added up, including "Other routes" and "No route", so it is the whole fleet's dollars.</description></item>
/// <item><term>VanOtherCurrencies</term><description>Van takings in any other currency, in none of the dollar figures.</description></item>
/// <item><term>Shop</term><description>The chosen shop's table; null when no shop is chosen.</description></item>
/// <item><term>Message</term><description>The WhatsApp text, exactly as it is sent.</description></item>
/// </list></remarks>
public sealed record DailySalesWhatsAppReport(
    DateTime TradingDate,
    List<DailySalesWhatsAppVanLine> VanLines,
    decimal VanTotal,
    List<DailySalesWhatsAppCurrencyAmount> VanOtherCurrencies,
    DailySalesWhatsAppShop? Shop,
    string Message,
    DateTime GeneratedAtUtc);

/// <param name="Label">The row's label, with the day's area when the row asks for it.</param>
/// <param name="Sales">US dollars, VAT included.</param>
/// <param name="Routes">The route codes the line adds up; empty for "No route".</param>
public sealed record DailySalesWhatsAppVanLine(string Label, decimal Sales, List<string> Routes);

/// <summary>The shop's till sales for the day.</summary>
/// <remarks><list type="table">
/// <item><term>Lines</term><description>Units sold per product row, before any credit note — what the till rang up, in any currency.</description></item>
/// <item><term>Total</term><description>Every dollar sale at the shop that day, VAT included — every item, not only the rows listed.</description></item>
/// <item><term>OtherCurrencies</term><description>The shop's takings in any other currency, in none of the dollar figures.</description></item>
/// </list></remarks>
public sealed record DailySalesWhatsAppShop(
    string WarehouseCode,
    string Heading,
    List<DailySalesWhatsAppProductLine> Lines,
    decimal Total,
    int SalesCount,
    List<DailySalesWhatsAppCurrencyAmount> OtherCurrencies);

public sealed record DailySalesWhatsAppProductLine(string Label, decimal Quantity, List<string> ItemCodes);

public sealed record DailySalesWhatsAppCurrencyAmount(string Currency, decimal Amount);
