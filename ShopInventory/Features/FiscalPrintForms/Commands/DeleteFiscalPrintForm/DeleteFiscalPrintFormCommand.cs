using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalPrintForms.Commands.DeleteFiscalPrintForm;

/// <summary>Removes a partner's choice, returning its sales to the A4 invoice default.</summary>
public sealed record DeleteFiscalPrintFormCommand(string CardCode, string? CallerName) : IRequest<ErrorOr<Deleted>>;
