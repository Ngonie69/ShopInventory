using ErrorOr;
using MediatR;

namespace ShopInventory.Features.MarketBreakages.Queries.GetMarketBreakagesPdf;

/// <summary>
/// The office list's reports for this status and search, every page of them, laid out as a PDF:
/// the figures, by van, by product, then each report with its lines.
/// </summary>
public sealed record GetMarketBreakagesPdfQuery(string? Status, string? Search)
    : IRequest<ErrorOr<MarketBreakagesDocument>>;

/// <summary>The rendered PDF and the name to save it under.</summary>
public sealed record MarketBreakagesDocument(byte[] Content, string FileName);
