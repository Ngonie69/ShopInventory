namespace ShopInventory.Web.Models;

public class BulkPodValidationResult
{
    public int DocNum { get; set; }
    public int? DocEntry { get; set; }
    public string? CardCode { get; set; }
    public string? CardName { get; set; }
    public bool Found { get; set; }
    public int ExistingPodCount { get; set; }
    public string? ErrorMessage { get; set; }
}