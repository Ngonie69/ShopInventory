namespace ShopInventory.Web.Components;

/// <summary>
/// The currency rows for the sales forms' <see cref="NocturneSelect{TValue}"/>.
/// </summary>
/// <remarks>
/// Invoice, sales order and credit note are raised in USD or ZIG only. The
/// quotation form offers a wider list and keeps its own.
/// </remarks>
public static class DocumentCurrencyOptions
{
    public static readonly NocturneSelectOption<string>[] UsdZig =
    [
        new("USD", "USD — US Dollar"),
        new("ZIG", "ZIG — Zimbabwe Gold")
    ];
}
