using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>The hub's headline figures: the period, the period before it, and what is owed today.</summary>
public sealed record ReportsHeadlineResult(
    ProfitOverviewReport? Current,
    ProfitOverviewReport? Previous,
    ReceivablesAgingReport? Aging,
    IReadOnlyCollection<string> Unavailable);
