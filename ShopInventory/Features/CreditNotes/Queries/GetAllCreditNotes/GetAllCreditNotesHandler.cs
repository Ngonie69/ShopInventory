using ErrorOr;
using MediatR;
using ShopInventory.Common.Fiscalization;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.CreditNotes.Queries.GetAllCreditNotes;

public sealed class GetAllCreditNotesHandler(
    ApplicationDbContext dbContext,
    ICreditNoteService creditNoteService
) : IRequestHandler<GetAllCreditNotesQuery, ErrorOr<CreditNoteListResponseDto>>
{
    public async Task<ErrorOr<CreditNoteListResponseDto>> Handle(
        GetAllCreditNotesQuery request,
        CancellationToken cancellationToken)
    {
        if (request.ListOptions is { } options)
            return await GetFilteredPageAsync(request, options, cancellationToken);

        var result = await creditNoteService.GetAllAsync(
            request.Page, request.PageSize, request.Status, request.CardCode,
            request.FromDate, request.ToDate, request.IncludeLines, request.VanSalesOnly, cancellationToken);

        await FiscalDocumentStatusProjector.EnrichCreditNotesAsync(dbContext, result.CreditNotes, cancellationToken);

        return result;
    }

    /// <summary>
    /// One page of the Credit Notes list. The page used to ask for the whole date range, up to ten
    /// thousand notes, and hold them for as long as it stayed open so it could filter, sort and page
    /// them itself. The range is still read here, one request at a time, and let go once the page is cut.
    /// </summary>
    private async Task<CreditNoteListResponseDto> GetFilteredPageAsync(
        GetAllCreditNotesQuery request, CreditNoteListOptions options, CancellationToken cancellationToken)
    {
        var range = await creditNoteService.GetAllAsync(
            1, int.MaxValue, request.Status, request.CardCode,
            request.FromDate, request.ToDate, includeLines: false, request.VanSalesOnly, cancellationToken);

        IEnumerable<CreditNoteDto> matching = range.CreditNotes;

        if (!string.IsNullOrWhiteSpace(options.CreditNoteNumber))
        {
            var number = options.CreditNoteNumber.Trim();
            matching = matching.Where(note => note.CreditNoteNumber?.Contains(number, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (!string.IsNullOrWhiteSpace(options.Customer))
        {
            var customer = options.Customer.Trim();
            matching = matching.Where(note =>
                note.CardCode?.Contains(customer, StringComparison.OrdinalIgnoreCase) == true
                || note.CardName?.Contains(customer, StringComparison.OrdinalIgnoreCase) == true);
        }

        var notes = matching.ToList();

        // The fiscal state is looked up rather than stored, so filtering on it means looking up every
        // match; without that filter only the page on screen is looked up.
        var fiscalFilter = string.IsNullOrWhiteSpace(options.FiscalStatus) ? null : NormalizeFiscalStatus(options.FiscalStatus);
        if (fiscalFilter is not null)
        {
            await FiscalDocumentStatusProjector.EnrichCreditNotesAsync(dbContext, notes, cancellationToken);
            notes = notes.Where(note => NormalizeFiscalStatus(note.FiscalizationStatus) == fiscalFilter).ToList();
        }

        var pageSize = Math.Max(1, request.PageSize);
        var page = Math.Max(1, request.Page);
        var pageRows = Sort(notes, options)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        if (fiscalFilter is null)
            await FiscalDocumentStatusProjector.EnrichCreditNotesAsync(dbContext, pageRows, cancellationToken);

        return new CreditNoteListResponseDto
        {
            Page = page,
            PageSize = pageSize,
            TotalCount = notes.Count,
            TotalPages = (int)Math.Ceiling(notes.Count / (double)pageSize),
            CreditNotes = pageRows
        };
    }

    private static IEnumerable<CreditNoteDto> Sort(IEnumerable<CreditNoteDto> notes, CreditNoteListOptions options)
    {
        var ordered = options.SortBy switch
        {
            CreditNoteListSort.Date => By(note => note.CreditNoteDate),
            CreditNoteListSort.Total => By(note => note.DocTotal),
            _ => By(note => note.SAPDocNum ?? note.Id)
        };

        // The page sorted in place, so equal keys could land on either side of a page break.
        return ordered.ThenByDescending(note => note.SAPDocEntry ?? note.Id);

        IOrderedEnumerable<CreditNoteDto> By<TKey>(Func<CreditNoteDto, TKey> key) =>
            options.SortDescending ? notes.OrderByDescending(key) : notes.OrderBy(key);
    }

    /// <summary>The page's three fiscal states; anything the projector writes besides the first two is Unknown.</summary>
    internal static string NormalizeFiscalStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "fiscalised" => "fiscalised",
            "not fiscalised" => "not fiscalised",
            _ => "unknown"
        };
}
