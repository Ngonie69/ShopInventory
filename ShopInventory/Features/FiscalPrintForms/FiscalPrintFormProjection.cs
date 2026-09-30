using ShopInventory.Models.Entities;

namespace ShopInventory.Features.FiscalPrintForms;

internal static class FiscalPrintFormProjection
{
    public static FiscalPrintFormDto ToDto(BusinessPartnerFiscalPrintFormEntity row) =>
        new(row.CardCode, row.CardName, row.PrintForm.ToString(), row.UpdatedAtUtc, row.UpdatedBy);
}
