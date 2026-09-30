using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.FiscalPrintForms.Queries.GetFiscalPrintForms;

/// <summary>Every business partner with a fiscal document type saved.</summary>
public sealed record GetFiscalPrintFormsQuery : IRequest<ErrorOr<List<FiscalPrintForm>>>;
