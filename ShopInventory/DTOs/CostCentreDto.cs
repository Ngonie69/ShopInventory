namespace ShopInventory.DTOs;

/// <summary>
/// DTO for Cost Centre (Profit Center) information from SAP
/// </summary>
public class CostCentreDto
{
    /// <summary>
    /// Cost centre code (e.g., "100", "200", "SALES")
    /// </summary>
    public string? CenterCode { get; set; }

    /// <summary>
    /// Cost centre name/description
    /// </summary>
    public string? CenterName { get; set; }

    /// <summary>
    /// Dimension type in SAP (1-5)
    /// </summary>
    public int Dimension { get; set; }

    /// <summary>
    /// Whether this cost centre is active
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Start date of validity
    /// </summary>
    public string? ValidFrom { get; set; }

    /// <summary>
    /// End date of validity
    /// </summary>
    public string? ValidTo { get; set; }

    /// <summary>
    /// Display helper for dropdowns
    /// </summary>
    public string DisplayName => $"{CenterCode} - {CenterName}";
}

/// <summary>
/// Response DTO for list of Cost Centres
/// </summary>
public class CostCentreListResponseDto
{
    public int TotalCount { get; set; }
    public List<CostCentreDto>? CostCentres { get; set; }
}
