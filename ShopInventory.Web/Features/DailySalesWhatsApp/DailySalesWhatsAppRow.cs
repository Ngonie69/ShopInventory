// Mirrors ShopInventory/Features/DailySalesWhatsApp/Queries/GetDailySalesWhatsAppSettings/DailySalesWhatsAppSettings.cs.

namespace ShopInventory.Web.Features.DailySalesWhatsApp;

/// <summary>One row of the van or shop table: its label and the route or item codes it adds up.</summary>
public sealed class DailySalesWhatsAppRow
{
    public string Label { get; set; } = string.Empty;
    public List<string> Codes { get; set; } = [];
    public bool ShowArea { get; set; }
}
