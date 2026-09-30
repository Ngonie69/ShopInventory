using ErrorOr;
using MediatR;

namespace ShopInventory.Features.FiscalPrintForms.Queries.GetFiscalPrintForms;

/// <summary>Every business partner with a fiscal document choice saved.</summary>
public sealed record GetFiscalPrintFormsQuery : IRequest<ErrorOr<List<FiscalPrintFormDto>>>;
