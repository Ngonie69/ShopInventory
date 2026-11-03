namespace ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;

/// <summary>The daily sales WhatsApp report as last saved from Settings, and what it last sent.</summary>
/// <remarks><list type="table">
/// <item><term>Enabled</term><description>Sent every morning from 07:15 CAT for the day before.</description></item>
/// <item><term>Recipients</term><description>WhatsApp numbers in E.164, <c>+263771234567</c>. Each gets their own message.</description></item>
/// <item><term>ShopWarehouseCode</term><description>The one shop whose till sales make the second table; null leaves the table out.</description></item>
/// <item><term>ShopName</term><description>That shop's name from the shops list; null when no shop is chosen or none has the warehouse.</description></item>
/// <item><term>ShopHeading</term><description>What the shop table is headed; null for "{shop name} Sales".</description></item>
/// <item><term>VanRows</term><description>The van table's rows, in order: a label and the route codes it adds up. Empty lists every route under its own name.</description></item>
/// <item><term>ProductRows</term><description>The shop table's rows, in order: a label and the item codes whose quantities it adds up.</description></item>
/// <item><term>LastSentFor</term><description>The last trading day the report went out for, and to whom in <c>LastSentTo</c>.</description></item>
/// </list></remarks>
public sealed record DailySalesWhatsAppSettings(
    bool Enabled,
    List<string> Recipients,
    string? ShopWarehouseCode,
    string? ShopName,
    string? ShopHeading,
    List<DailySalesWhatsAppRow> VanRows,
    List<DailySalesWhatsAppRow> ProductRows,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc,
    DateTime? LastSentFor,
    List<string> LastSentTo);

/// <summary>One row of either table: what it is called and the codes it adds up.</summary>
/// <param name="Label">What the row reads as, e.g. "CBD / Chitungwiza" or "5Ltr Cortina Ice-cream".</param>
/// <param name="Codes">Route codes for a van row, item codes for a product row. Several are added together.</param>
/// <param name="ShowArea">Van rows only: append where the truck worked that day, as in "UPC 1 - Zaka".</param>
public sealed record DailySalesWhatsAppRow(string Label, List<string> Codes, bool ShowArea = false);
