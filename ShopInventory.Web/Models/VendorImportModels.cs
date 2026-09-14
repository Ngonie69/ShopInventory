namespace ShopInventory.Web.Models;

/// <summary>The web copy of the API's <c>ImportVendorsRequest</c>.</summary>
public sealed class ImportVendorsRequestModel
{
    public bool ValidateOnly { get; set; } = true;

    public List<ImportVendorRowModel> Rows { get; set; } = [];
}

/// <summary>One vendor as read off the upload template — the web copy of <c>ImportVendorRow</c>.</summary>
public sealed class ImportVendorRowModel
{
    public int RowNumber { get; set; }

    public string? Depot { get; set; }

    public string? Code { get; set; }

    public string? Name { get; set; }

    public string? Surname { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? VatNumber { get; set; }
}

/// <summary>The web copy of the API's <c>ImportVendorsResultDto</c>.</summary>
public sealed class ImportVendorsResultModel
{
    public bool Imported { get; set; }

    public int CreateCount { get; set; }

    public int RestoreCount { get; set; }

    public int ErrorCount { get; set; }

    public List<ImportVendorRowResultModel> Rows { get; set; } = [];
}

/// <summary>The web copy of the API's <c>ImportVendorRowResult</c>.</summary>
public sealed class ImportVendorRowResultModel
{
    public int RowNumber { get; set; }

    public string? Code { get; set; }

    public bool CodeIssued { get; set; }

    public string? Depot { get; set; }

    public string? Name { get; set; }

    public string? Surname { get; set; }

    /// <summary><c>Create</c>, <c>Restore</c> or <c>Error</c>.</summary>
    public string Action { get; set; } = "Error";

    public int? VendorId { get; set; }

    public List<string> Errors { get; set; } = [];
}
