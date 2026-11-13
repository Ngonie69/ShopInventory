using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.Vending.Queries.ExportVendingReconciliation;

/// <summary>
/// The Reconciliation workbook for a range, read fresh from the API. <paramref name="WarehouseCode"/>
/// narrows it to one depot warehouse; <paramref name="ScopeLabel"/> is what the title calls that choice.
/// </summary>
public sealed record ExportVendingReconciliationQuery(DateTime From, DateTime To, string? WarehouseCode, string ScopeLabel)
    : IRequest<ErrorOr<ExportVendingReconciliationResult>>;
