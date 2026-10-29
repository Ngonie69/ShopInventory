namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>One report in the hub's directory, and the roles that may open it.</summary>
public sealed record ReportsDirectoryEntry(string Name, string Description, string Href, string Roles);
