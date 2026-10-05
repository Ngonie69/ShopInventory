using System.Globalization;
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

    /// <summary>
    /// Matches the report number (with or without its #), the rep, van, shop, or an item's code or
    /// description, ignoring case.
    /// </summary>
    public static IQueryable<MarketBreakageEntity> Search(IQueryable<MarketBreakageEntity> breakages, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return breakages;

        var text = search.Trim();
        var pattern = $"%{text.ToLower()}%";
        int? number = int.TryParse(text.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

        return breakages.Where(breakage =>
            (number != null && breakage.Id == number)
            || EF.Functions.Like(breakage.ReportedByName.ToLower(), pattern)
            || EF.Functions.Like(breakage.VanWarehouseCode.ToLower(), pattern)
            || (breakage.CardCode != null && EF.Functions.Like(breakage.CardCode.ToLower(), pattern))
            || (breakage.CardName != null && EF.Functions.Like(breakage.CardName.ToLower(), pattern))
            || breakage.Lines.Any(line => EF.Functions.Like(line.ItemCode.ToLower(), pattern)
                                          || (line.ItemDescription != null && EF.Functions.Like(line.ItemDescription.ToLower(), pattern))));
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

    /// <summary>
    /// The list's order for <paramref name="status"/>: the open view puts what needs the office first —
    /// a failed transfer, then a stranded one — and the oldest report before newer ones, so the queue is
    /// worked from the top. Every other view is <see cref="Newest"/>.
    /// </summary>
    public static IOrderedQueryable<MarketBreakageEntity> ListOrder(IQueryable<MarketBreakageEntity> breakages, string? status)
    {
        if (!string.Equals(status, Open, StringComparison.OrdinalIgnoreCase))
            return Newest(breakages);

        return breakages
            .OrderBy(breakage => breakage.Status == MarketBreakageStatuses.TransferFailed ? 0
                : breakage.Status == MarketBreakageStatuses.Transferring ? 1
                : 2)
            .ThenBy(breakage => breakage.CapturedAtUtc)
            .ThenBy(breakage => breakage.Id);
    }

    /// <summary>Newest first: the history views and the exports.</summary>
    public static IOrderedQueryable<MarketBreakageEntity> Newest(IQueryable<MarketBreakageEntity> breakages)
        => breakages
            .OrderByDescending(breakage => breakage.CreatedAtUtc)
            .ThenByDescending(breakage => breakage.Id);
}
