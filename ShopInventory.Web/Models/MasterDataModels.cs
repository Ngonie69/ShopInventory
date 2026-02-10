namespace ShopInventory.Web.Models;

public class GLAccountDto
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? AccountType { get; set; }
    public string? Currency { get; set; }
    public decimal Balance { get; set; }
    public bool IsActive { get; set; }

    // Display helper
    public string DisplayName => $"{Code} - {Name}";
}

public class GLAccountListResponse
{
    public int TotalCount { get; set; }
    public List<GLAccountDto>? Accounts { get; set; }
}

/// <summary>
/// Cost Centre (Profit Center) DTO
/// </summary>
public class CostCentreDto
{
    public string? CenterCode { get; set; }
    public string? CenterName { get; set; }
    public int Dimension { get; set; }
    public bool IsActive { get; set; }
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }

    // Display helper
    public string DisplayName => $"{CenterCode} - {CenterName}";
}

public class CostCentreListResponse
{
    public int TotalCount { get; set; }
    public List<CostCentreDto>? CostCentres { get; set; }
}

public class WarehouseDto
{
    public string? WarehouseCode { get; set; }
    public string? WarehouseName { get; set; }
    public string? Location { get; set; }
    public string? Street { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public bool IsActive { get; set; }

    // Display helper
    public string DisplayName => $"{WarehouseCode} - {WarehouseName}";
}

public class WarehouseListResponse
{
    public int TotalWarehouses { get; set; }
    public List<WarehouseDto>? Warehouses { get; set; }
}
