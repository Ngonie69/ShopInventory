using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetReportsOverview;

/// <summary>
/// The hub's stock warnings. Read on their own because SAP stock reads can hang for a minute or
/// more, and the rest of the page should not wait for them.
/// </summary>
public sealed record GetReportsStockWatchQuery(DateOnly Today) : IRequest<ErrorOr<ReportsStockWatchResult>>
{
    /// <summary>An item is slow when it has not sold for this many days.</summary>
    public const int SlowMovingDays = 60;

    /// <summary>How far back a sale is looked for; an item with none in this window reads as never sold.</summary>
    public const int SalesLookbackDays = 180;
}
