namespace ShopInventory.Web.Features.FiscalPrintForms;

/// <summary>The two document types the platform can file a receipt as.</summary>
public static class FiscalDocumentTypes
{
    public const string Receipt48 = "Receipt48";
    public const string InvoiceA4 = "InvoiceA4";

    public static string Label(string? printForm) =>
        string.Equals(printForm, Receipt48, StringComparison.OrdinalIgnoreCase) ? "48 mm receipt" : "A4 invoice";
}
