using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VendingCashUps.Queries.GetVendingCashUpSheet;

/// <summary>One cashier's day at one warehouse, counted or not, for the capture form.</summary>
public sealed record GetVendingCashUpSheetQuery(
    Guid CashierUserId,
    string WarehouseCode,
    DateTime BusinessDate,
    Guid? CallerUserId
) : IRequest<ErrorOr<VendingCashUpSheetDto>>;
