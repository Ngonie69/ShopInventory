using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// Invoiced-less-credited quantity, converted volume, and net revenue for a chosen set of business
/// partners and items over a date range.
/// </summary>
/// <remarks>
/// An empty <c>ItemCodes</c> means every item the selected accounts traded in the range.
/// </remarks>
public sealed record GetItemVolumeSalesReportQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    ItemVolumeSalesGrouping Grouping,
    IReadOnlyList<string> AccountCodes,
    IReadOnlyList<string> ItemCodes
) : IRequest<ErrorOr<GetItemVolumeSalesReportResult>>;
