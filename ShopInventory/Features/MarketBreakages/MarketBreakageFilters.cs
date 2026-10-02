using Microsoft.EntityFrameworkCore;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.MarketBreakages;

/// <summary>
/// The office list's search and status filter, shared so an export holds exactly the reports the page
/// was showing.
/// </summary>
public static class MarketBreakageFilters
{
    /// <summary>Everything still waiting on the office: pending, failed or stranded.</summary>
    public const string Open = "open";

    /// <summary>Matches the rep, van, shop or an item code, ignoring case.</summary>
    public static IQueryable<MarketBreakageEntity> Search(IQueryable<MarketBreakageEntity> breakages, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return breakages;

        var pattern = $"%{search.Trim().ToLower()}%";
        return breakages.Where(breakage =>
            EF.Functions.Like(breakage.ReportedByName.ToLower(), pattern)
            || EF.Functions.Like(breakage.VanWarehouseCode.ToLower(), pattern)
            || (breakage.CardCode != null && EF.Functions.Like(breakage.CardCode.ToLower(), pattern))
            || (breakage.CardName != null && EF.Functions.Like(breakage.CardName.ToLower(), pattern))
            || breakage.Lines.Any(line => EF.Functions.Like(line.ItemCode.ToLower(), pattern)));
    }

    /// <summary><see cref="Open"/>, one of <see cref="MarketBreakageStatuses.All"/>, or empty for all.</summary>
    public static IQueryable<MarketBreakageEntity> Status(IQueryable<MarketBreakageEntity> breakages, string? status)
    {
        if (string.Equals(status, Open, StringComparison.OrdinalIgnoreCase))
        {
            return breakages.Where(breakage =>
                breakage.Status == MarketBreakageStatuses.Pending
                || breakage.Status == MarketBreakageStatuses.TransferFailed
                || breakage.Status == MarketBreakageStatuses.Transferring);
        }

        if (string.IsNullOrWhiteSpace(status))
            return breakages;

        var exact = MarketBreakageStatuses.All.First(value =>
            string.Equals(value, status, StringComparison.OrdinalIgnoreCase));
        return breakages.Where(breakage => breakage.Status == exact);
    }

    /// <summary>Whether <paramref name="status"/> is a filter <see cref="Status"/> understands.</summary>
    public static bool IsKnownStatus(string? status)
        => string.IsNullOrWhiteSpace(status)
           || string.Equals(status, Open, StringComparison.OrdinalIgnoreCase)
           || MarketBreakageStatuses.All.Contains(status, StringComparer.OrdinalIgnoreCase);

    public static string StatusMessage => $"Status must be open or one of: {string.Join(", ", MarketBreakageStatuses.All)}.";

    /// <summary>The list's order: newest first.</summary>
    public static IOrderedQueryable<MarketBreakageEntity> Newest(IQueryable<MarketBreakageEntity> breakages)
        => breakages
            .OrderByDescending(breakage => breakage.CreatedAtUtc)
            .ThenByDescending(breakage => breakage.Id);
}
