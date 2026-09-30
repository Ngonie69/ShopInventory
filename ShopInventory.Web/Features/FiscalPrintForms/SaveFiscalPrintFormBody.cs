namespace ShopInventory.Web.Features.FiscalPrintForms;

/// <summary>The body of <c>PUT api/fiscalisation-settings/print-forms/{cardCode}</c>.</summary>
public sealed record SaveFiscalPrintFormBody(string? CardName, string PrintForm);
