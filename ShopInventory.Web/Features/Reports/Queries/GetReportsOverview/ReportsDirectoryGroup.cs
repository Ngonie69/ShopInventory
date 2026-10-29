namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>A heading in the hub's directory and the reports under it.</summary>
public sealed record ReportsDirectoryGroup(string Name, IReadOnlyList<ReportsDirectoryEntry> Entries);
