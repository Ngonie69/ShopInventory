namespace ShopInventory.Features.FiscalPrintForms.Commands.SaveFiscalPrintForm;

/// <summary>The body of <c>PUT api/fiscalisation-settings/print-forms/{cardCode}</c>.</summary>
public sealed record SaveFiscalPrintFormRequest(string? CardName, string PrintForm);
