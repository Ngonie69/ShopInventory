using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Queries.GetCreditNoteByDocNum;

public sealed class GetCreditNoteByDocNumHandler(
    ApplicationDbContext context
) : IRequestHandler<GetCreditNoteByDocNumQuery, ErrorOr<CreditNoteDto>>
{
    public async Task<ErrorOr<CreditNoteDto>> Handle(
        GetCreditNoteByDocNumQuery query,
        CancellationToken cancellationToken)
    {
        var creditNote = await context.CreditNotes
            .AsNoTracking()
            .Where(note => note.SAPDocNum == query.DocNum)
            .Select(note => new CreditNoteDto
            {
                Id = note.Id,
                SAPDocEntry = note.SAPDocEntry,
                SAPDocNum = note.SAPDocNum,
                CreditNoteNumber = note.CreditNoteNumber,
                CreditNoteDate = note.CreditNoteDate,
                CardCode = note.CardCode,
                CardName = note.CardName,
                Type = note.Type,
                Status = note.Status,
                OriginalInvoiceId = note.OriginalInvoiceId,
                OriginalInvoiceDocEntry = note.OriginalInvoiceDocEntry,
                Reason = note.Reason,
                Comments = note.Comments,
                Currency = note.Currency,
                ExchangeRate = note.ExchangeRate,
                SubTotal = note.SubTotal,
                TaxAmount = note.TaxAmount,
                DocTotal = note.DocTotal,
                AppliedAmount = note.AppliedAmount,
                Balance = note.Balance,
                RestockItems = note.RestockItems,
                RestockWarehouseCode = note.RestockWarehouseCode,
                CreatedByUserId = note.CreatedByUserId,
                ApprovedByUserId = note.ApprovedByUserId,
                ApprovedDate = note.ApprovedDate,
                CreatedAt = note.CreatedAt,
                UpdatedAt = note.UpdatedAt,
                IsSynced = note.IsSynced,
                Lines = note.Lines
                    .OrderBy(line => line.LineNum)
                    .Select(line => new CreditNoteLineDto
                    {
                        Id = line.Id,
                        LineNum = line.LineNum,
                        ItemCode = line.ItemCode,
                        ItemDescription = line.ItemDescription,
                        Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice,
                        DiscountPercent = line.DiscountPercent,
                        TaxPercent = line.TaxPercent,
                        LineTotal = line.LineTotal,
                        WarehouseCode = line.WarehouseCode,
                        ReturnReason = line.ReturnReason,
                        BatchNumber = line.BatchNumber,
                        IsRestocked = line.IsRestocked
                    })
                    .ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (creditNote is null)
            return Errors.CreditNote.NotFoundByNumber(query.DocNum.ToString());

        return creditNote;
    }
}