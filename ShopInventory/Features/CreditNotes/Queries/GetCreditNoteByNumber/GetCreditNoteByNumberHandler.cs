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
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient
) : IRequestHandler<GetCreditNoteByNumberQuery, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        GetCreditNoteByNumberQuery request,
        CancellationToken cancellationToken)
    {
        var sapDocNum = TryParseSapDocNum(request.CreditNoteNumber);
        CreditNoteDto? creditNote = null;

        if (sapDocNum.HasValue)
        {
            creditNote = await ResolveBySapDocNumAsync(sapDocNum.Value, cancellationToken);
        }

        creditNote ??= await creditNoteService.GetByCreditNoteNumberAsync(request.CreditNoteNumber, cancellationToken);

        if (creditNote is null)
            return Errors.CreditNote.NotFoundByNumber(request.CreditNoteNumber);

        await FiscalDocumentStatusProjector.EnrichCreditNoteAsync(context, creditNote, cancellationToken);

        return creditNote;
    }

    private async Task<CreditNoteDto?> ResolveBySapDocNumAsync(int sapDocNum, CancellationToken cancellationToken)
    {
        var localMatch = await context.CreditNotes
            .AsNoTracking()
            .Where(note => note.SAPDocNum == sapDocNum)
            .Select(note => new
            {
                note.Id,
                note.SAPDocEntry
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (localMatch?.SAPDocEntry is int sapDocEntry && sapDocEntry > 0)
        {
            var creditNote = await creditNoteService.GetByIdAsync(sapDocEntry, cancellationToken);
            if (creditNote is not null)
            {
                return creditNote;
            }
        }

        var sapCreditNote = await sapClient.GetCreditNoteByDocNumAsync(sapDocNum, cancellationToken);
        if (sapCreditNote is null)
        {
            if (localMatch?.Id > 0)
            {
                return await creditNoteService.GetByIdAsync(localMatch.Id, cancellationToken);
            }

            return null;
        }

        return await creditNoteService.GetByIdAsync(sapCreditNote.DocEntry, cancellationToken);
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
