using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;

namespace ShopInventory.Common.Fiscalization;

internal static class FiscalDocumentStatusProjector
{
    private const string InvoiceDocumentType = "Invoice";
    private const string CreditNoteDocumentType = "CreditNote";
    private const string FiscalisedStatus = "Fiscalised";
    private const string NotFiscalisedStatus = "Not Fiscalised";
    private const string UnknownStatus = "Unknown";

    public static Task EnrichInvoicesAsync(
        ApplicationDbContext dbContext,
        IEnumerable<InvoiceDto>? invoices,
        CancellationToken cancellationToken)
        => EnrichAsync(
            dbContext,
            InvoiceDocumentType,
            invoices,
            invoice => invoice.DocNum,
            ApplyInvoiceStatus,
            cancellationToken);

    public static Task EnrichInvoiceAsync(
        ApplicationDbContext dbContext,
        InvoiceDto? invoice,
        CancellationToken cancellationToken)
        => EnrichInvoicesAsync(
            dbContext,
            invoice is null ? null : new[] { invoice },
            cancellationToken);

    public static Task EnrichCreditNotesAsync(
        ApplicationDbContext dbContext,
        IEnumerable<CreditNoteDto>? creditNotes,
        CancellationToken cancellationToken)
        => EnrichAsync(
            dbContext,
            CreditNoteDocumentType,
            creditNotes,
            creditNote => creditNote.SAPDocNum.GetValueOrDefault(),
            ApplyCreditNoteStatus,
            cancellationToken);

    public static Task EnrichCreditNoteAsync(
        ApplicationDbContext dbContext,
        CreditNoteDto? creditNote,
        CancellationToken cancellationToken)
        => EnrichCreditNotesAsync(
            dbContext,
            creditNote is null ? null : new[] { creditNote },
            cancellationToken);

    private static async Task EnrichAsync<TDocument>(
        ApplicationDbContext dbContext,
        string documentType,
        IEnumerable<TDocument>? documents,
        Func<TDocument, int> docNumSelector,
        Action<TDocument, DesktopFiscalTransactionEntity?> applyStatus,
        CancellationToken cancellationToken)
    {
        if (documents is null)
        {
            return;
        }

        var documentList = documents.ToList();
        if (documentList.Count == 0)
        {
            return;
        }

        foreach (var document in documentList)
        {
            applyStatus(document, null);
        }

        var docNums = documentList
            .Select(docNumSelector)
            .Where(docNum => docNum > 0)
            .Distinct()
            .ToList();

        if (docNums.Count == 0)
        {
            return;
        }

        var latestTransactions = await dbContext.DesktopFiscalTransactions
            .AsNoTracking()
            .Where(transaction => transaction.DocumentType == documentType && docNums.Contains(transaction.DocNum))
            .OrderByDescending(transaction => transaction.LastSyncedAtUtc)
            .ThenByDescending(transaction => transaction.TimestampUtc)
            .ToListAsync(cancellationToken);

        var transactionLookup = latestTransactions
            .GroupBy(transaction => transaction.DocNum)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var document in documentList)
        {
            transactionLookup.TryGetValue(docNumSelector(document), out var transaction);
            applyStatus(document, transaction);
        }
    }

    private static void ApplyInvoiceStatus(InvoiceDto invoice, DesktopFiscalTransactionEntity? transaction)
    {
        var (isFiscalized, status) = ResolveStatus(transaction);
        invoice.IsFiscalized = isFiscalized;
        invoice.FiscalizationStatus = status;
        invoice.FiscalReceiptGlobalNo = transaction?.ReceiptGlobalNo;
        invoice.FiscalizedAtUtc = isFiscalized == true ? transaction?.TimestampUtc : null;
    }

    private static void ApplyCreditNoteStatus(CreditNoteDto creditNote, DesktopFiscalTransactionEntity? transaction)
    {
        var (isFiscalized, status) = ResolveStatus(transaction);
        creditNote.IsFiscalized = isFiscalized;
        creditNote.FiscalizationStatus = status;
        creditNote.FiscalReceiptGlobalNo = transaction?.ReceiptGlobalNo;
        creditNote.FiscalizedAtUtc = isFiscalized == true ? transaction?.TimestampUtc : null;
    }

    private static (bool? IsFiscalized, string Status) ResolveStatus(DesktopFiscalTransactionEntity? transaction)
    {
        if (transaction is null)
        {
            return (null, UnknownStatus);
        }

        if (HasFiscalEvidence(transaction))
        {
            return (true, FiscalisedStatus);
        }

        return (false, NotFiscalisedStatus);
    }

    private static bool HasFiscalEvidence(DesktopFiscalTransactionEntity transaction)
        => string.Equals(transaction.Status, "Success", StringComparison.OrdinalIgnoreCase)
           || string.Equals(transaction.Status, FiscalisedStatus, StringComparison.OrdinalIgnoreCase)
           || transaction.ReceiptGlobalNo.HasValue
           || !string.IsNullOrWhiteSpace(transaction.QRCode)
           || !string.IsNullOrWhiteSpace(transaction.VerificationCode);
}