namespace ShopInventory.Configuration;

public class SAPSettings
{
    public bool Enabled { get; set; }
    public string ServiceLayerUrl { get; set; } = string.Empty;
    public string CompanyDB { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Whether to use custom UDF fields (U_PackagingCode, U_PackagingCodeLabels, U_PackagingCodeLids).
    /// Set to true for production database, false for test database.
    /// </summary>
    public bool UseCustomFields { get; set; } = true;
}
