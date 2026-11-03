using MediatR;

namespace ShopInventory.Web.Features.Reports.MonthOnMonth;

/// <summary>
/// The product master as the month-on-month report needs it: every cached item with its name and SAP item
/// group, inactive items included, and SAP's item group names.
/// </summary>
public sealed record GetMonthOnMonthItemMasterQuery : IRequest<MonthOnMonthItemMaster>;

public sealed record MonthOnMonthItemMaster(
    IReadOnlyDictionary<string, MonthOnMonthItemInfo> Items,
    IReadOnlyDictionary<int, string> ItemGroupNames);
