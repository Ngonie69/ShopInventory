using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.MarketBreakages.Queries.ExportMarketBreakages;

public enum MarketBreakageExportFormat
{
    Excel,
    Pdf
}

/// <summary>
/// The reports the list holds for this status and search — every page, not just the one on screen — as
/// a file to download.
/// </summary>
public sealed record ExportMarketBreakagesQuery(string? Status, string? Search, MarketBreakageExportFormat Format)
    : IRequest<ErrorOr<MarketBreakageExportFile>>;

public sealed record MarketBreakageExportFile(byte[] Content, string FileName, string ContentType);
