using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.FiscalPrintForms.Commands.DeleteFiscalPrintForm;

/// <summary>Returns a partner to the A4 invoice default.</summary>
public sealed record DeleteFiscalPrintFormCommand(string CardCode) : IRequest<ErrorOr<Success>>;
