// Mirrors ShopInventory/Features/DailySalesWhatsApp/Queries/GetDailySalesWhatsAppSettings/DailySalesWhatsAppSettings.cs.
// Nullability matches the API exactly.

namespace ShopInventory.Web.Features.DailySalesWhatsApp;

public sealed class DailySalesWhatsAppSettings
{
    public bool Enabled { get; set; }
    public List<string> Recipients { get; set; } = [];
    public string? ShopWarehouseCode { get; set; }
    public string? ShopName { get; set; }
    public string? ShopHeading { get; set; }
    public List<DailySalesWhatsAppRow> VanRows { get; set; } = [];
    public List<DailySalesWhatsAppRow> ProductRows { get; set; } = [];
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? LastSentFor { get; set; }
    public List<string> LastSentTo { get; set; } = [];
}
