using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;

namespace ShopInventory.Features.VendingCashUps.Queries.GetVendingCashUpSheet;

public sealed class GetVendingCashUpSheetHandler(ApplicationDbContext db)
    : IRequestHandler<GetVendingCashUpSheetQuery, ErrorOr<VendingCashUpSheetDto>>
{
    public async Task<ErrorOr<VendingCashUpSheetDto>> Handle(
        GetVendingCashUpSheetQuery query,
        CancellationToken cancellationToken)
    {
        var isCashier = await db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == query.CashierUserId && user.Role == ApplicationRoles.CartVendor, cancellationToken);
        var businessDate = query.BusinessDate.Date;
        var warehouse = VendingCashUpSheets.NormalizeWarehouse(query.WarehouseCode);

        var cashUp = await VendingCashUpSheets.FindAsync(
            db, query.CashierUserId, warehouse, businessDate, tracking: false, cancellationToken);

        // A cash-up outlives its cashier's account, so one on file is shown even when the account is gone.
        if (!isCashier && cashUp is null)
        {
            return Errors.VendingCashUp.CashierNotFound;
        }

        return await VendingCashUpSheets.BuildAsync(
            db, query.CashierUserId, warehouse, businessDate, cashUp, query.CallerUserId, cancellationToken);
    }
}
