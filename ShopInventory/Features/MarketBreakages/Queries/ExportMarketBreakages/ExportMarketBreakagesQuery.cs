using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.MarketBreakages.Queries.ExportMarketBreakages;

/// <summary>
/// Every report the office list shows for this status and search, every page of it, with the lines:
/// the data behind the Excel and PDF exports.
/// </summary>
public sealed record ExportMarketBreakagesQuery(string? Status, string? Search)
    : IRequest<ErrorOr<MarketBreakageExportDto>>;
