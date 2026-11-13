using FluentValidation;

namespace ShopInventory.Features.VendingCashUps.Queries.GetVendingCashUpSheet;

public sealed class GetVendingCashUpSheetValidator : AbstractValidator<GetVendingCashUpSheetQuery>
{
    public GetVendingCashUpSheetValidator()
    {
        RuleFor(query => query.CashierUserId).NotEmpty();
        RuleFor(query => query.WarehouseCode).NotEmpty().MaximumLength(50);
    }
}
