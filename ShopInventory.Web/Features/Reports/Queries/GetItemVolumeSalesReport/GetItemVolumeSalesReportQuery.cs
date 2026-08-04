using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Reports.Queries.GetItemVolumeSalesReport;

/// <summary>
/// The page-facing shape of the item volume and revenue report request.
/// </summary>
/// <remarks>
/// The codes arrive as the raw text the user typed or picked, so the handler owns the splitting.
/// An empty <c>ItemCodesText</c> asks for every item the selected accounts traded.
/// </remarks>
public sealed record GetItemVolumeSalesReportQuery(
    DateTime? FromDate,
    DateTime? ToDate,
    ItemVolumeSalesGrouping Grouping,
    string AccountCodesText,
    string ItemCodesText
) : IRequest<ErrorOr<GetItemVolumeSalesReportResult>>;
