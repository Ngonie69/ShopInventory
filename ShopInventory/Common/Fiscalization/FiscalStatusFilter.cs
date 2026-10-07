namespace ShopInventory.Common.Fiscalization;

/// <summary>
/// The three fiscal states the list pages filter on. <see cref="FiscalDocumentStatusProjector"/> writes
/// "Fiscalised", "Not Fiscalised" or "Unknown"; anything else reads as Unknown, as the pages always read it.
/// </summary>
public static class FiscalStatusFilter
{
    public const string Fiscalised = "fiscalised";

    public static string Normalize(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "fiscalised" => Fiscalised,
            "not fiscalised" => "not fiscalised",
            _ => "unknown"
        };
}
