namespace ShopInventory.Features.Merchandiser.Commands.BackfillMobileOrderTax;

public sealed record BackfillMobileOrderTaxResult(
    int OrdersUpdated,
    int LinesUpdated,
    decimal AppliedTaxPercent
);