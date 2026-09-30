using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.FiscalPrintForms.Commands.SaveFiscalPrintForm;

/// <summary>Sets whether a partner's till, vending and van sales are fiscalised as a 48 mm receipt or an A4 invoice.</summary>
public sealed record SaveFiscalPrintFormCommand(string CardCode, SaveFiscalPrintFormBody Body) : IRequest<ErrorOr<FiscalPrintForm>>;
