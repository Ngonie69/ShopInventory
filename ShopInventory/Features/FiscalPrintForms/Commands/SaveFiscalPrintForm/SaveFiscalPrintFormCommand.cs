using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalPrintForms.Commands.SaveFiscalPrintForm;

/// <summary>
/// Sets whether a business partner's till, vending and van sales are fiscalised as a 48 mm receipt or an
/// A4 invoice.
/// </summary>
public sealed record SaveFiscalPrintFormCommand(
    string CardCode,
    string? CardName,
    string PrintForm,
    string? CallerName
) : IRequest<ErrorOr<FiscalPrintFormDto>>;
