namespace ShopInventory.DTOs;

/// <summary>
/// DTO for G/L Account information
/// </summary>
public class GLAccountDto
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? AccountType { get; set; }
    public string? Currency { get; set; }
    public decimal Balance { get; set; }
    public bool IsActive { get; set; }
}

/// <summary>
/// Response DTO for list of G/L Accounts
/// </summary>
public class GLAccountListResponseDto
{
    public int TotalCount { get; set; }
    public List<GLAccountDto>? Accounts { get; set; }
}
