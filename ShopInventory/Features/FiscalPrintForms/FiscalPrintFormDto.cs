namespace ShopInventory.Features.FiscalPrintForms;

/// <summary>
/// One business partner's fiscal document choice. <see cref="PrintForm"/> is <c>Receipt48</c> or
/// <c>InvoiceA4</c>.
/// </summary>
public sealed record FiscalPrintFormDto(
    string CardCode,
    string? CardName,
    string PrintForm,
    DateTime UpdatedAtUtc,
    string? UpdatedBy);
