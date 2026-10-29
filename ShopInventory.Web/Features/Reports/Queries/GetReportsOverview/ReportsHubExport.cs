namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>What the Reports Hub was showing, handed to the Excel export as one piece.</summary>
public sealed record ReportsHubExport(
    ReportsPeriod Period,
    ReportsHeadlineResult? Headline,
    ReportsActivityResult? Activity,
    IReadOnlyList<ReportsAttentionItem> Attention);
