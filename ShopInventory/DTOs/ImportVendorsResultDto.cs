namespace ShopInventory.DTOs;

/// <summary>What a vendor upload did, or would do, row by row.</summary>
public sealed class ImportVendorsResultDto
{
    /// <summary>True only when the rows were saved: a check, or a file with any problem, saves nothing.</summary>
    public bool Imported { get; set; }

    /// <summary>Rows that would add — or did add — a new vendor.</summary>
    public int CreateCount { get; set; }

    /// <summary>Rows naming a removed vendor at the same depot, which bring that vendor back.</summary>
    public int RestoreCount { get; set; }

    public int ErrorCount { get; set; }

    public List<ImportVendorRowResult> Rows { get; set; } = [];
}

/// <summary>One sheet row, resolved.</summary>
public sealed class ImportVendorRowResult
{
    public int RowNumber { get; set; }

    /// <summary>The code the vendor takes — the one in the sheet, or the one issued for a blank.</summary>
    public string? Code { get; set; }

    /// <summary>True when the sheet left the code blank and <see cref="Code"/> was issued for it.</summary>
    public bool CodeIssued { get; set; }

    /// <summary>The depot's business partner code, once the row has been placed at one.</summary>
    public string? Depot { get; set; }

    public string? Name { get; set; }

    public string? Surname { get; set; }

    /// <summary><c>Create</c>, <c>Restore</c> or <c>Error</c>.</summary>
    public string Action { get; set; } = ImportVendorActions.Error;

    /// <summary>Set once saved.</summary>
    public int? VendorId { get; set; }

    public List<string> Errors { get; set; } = [];
}

public static class ImportVendorActions
{
    public const string Create = "Create";
    public const string Restore = "Restore";
    public const string Error = "Error";
}
