namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>One line of the hub's "Needs attention" list, and the report that explains it.</summary>
public sealed record ReportsAttentionItem(
    ReportsAttentionSeverity Severity,
    string Title,
    string Detail,
    string LinkText,
    string Href);
