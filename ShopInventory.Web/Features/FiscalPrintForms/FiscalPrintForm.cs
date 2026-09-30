namespace ShopInventory.Web.Features.FiscalPrintForms;

/// <summary>
/// The fiscal document a partner's till, vending and van sales are filed as. Mirrors the API's
/// <c>FiscalPrintFormDto</c>; <see cref="PrintForm"/> is <see cref="FiscalDocumentTypes.Receipt48"/> or
/// <see cref="FiscalDocumentTypes.InvoiceA4"/>.
/// </summary>
public sealed record FiscalPrintForm(
    string CardCode,
    string? CardName,
    string PrintForm,
    DateTime UpdatedAtUtc,
    string? UpdatedBy);
