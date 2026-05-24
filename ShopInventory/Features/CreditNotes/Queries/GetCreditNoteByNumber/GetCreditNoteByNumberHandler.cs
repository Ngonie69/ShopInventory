using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.GetCreditNoteByNumber;

public sealed class GetCreditNoteByNumberHandler(
    ICreditNoteService creditNoteService,
    ApplicationDbContext context
) : IRequestHandler<GetCreditNoteByNumberQuery, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        GetCreditNoteByNumberQuery request,
        CancellationToken cancellationToken)
    {
        var creditNote = await creditNoteService.GetByCreditNoteNumberAsync(request.CreditNoteNumber, cancellationToken);

        if (creditNote is null)
        {
            var sapDocNum = TryParseSapDocNum(request.CreditNoteNumber);
            if (sapDocNum.HasValue)
            {
                var resolvedDocEntry = await context.CreditNotes
                    .AsNoTracking()
                    .Where(note => note.SAPDocNum == sapDocNum.Value)
                    .Select(note => note.SAPDocEntry ?? note.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (resolvedDocEntry > 0)
                {
                    creditNote = await creditNoteService.GetByIdAsync(resolvedDocEntry, cancellationToken);
                }
            }
        }

        if (creditNote is null)
            return Errors.CreditNote.NotFoundByNumber(request.CreditNoteNumber);

        await FiscalDocumentStatusProjector.EnrichCreditNoteAsync(context, creditNote, cancellationToken);

        return creditNote;
    }

    private static int? TryParseSapDocNum(string creditNoteNumber)
    {
        var trimmed = creditNoteNumber.Trim();

        if (int.TryParse(trimmed, out var rawDocNum))
        {
            return rawDocNum;
        }

        const string sapPrefix = "SAP-CN-";
        if (trimmed.StartsWith(sapPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed[sapPrefix.Length..], out var prefixedDocNum))
        {
            return prefixedDocNum;
        }

        return null;
    }
}
