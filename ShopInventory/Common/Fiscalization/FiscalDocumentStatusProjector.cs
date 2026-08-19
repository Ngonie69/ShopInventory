using System.Linq.Expressions;
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

    /// <summary>
    /// Whether a fiscal transaction row is evidence that ZIMRA holds a receipt for its document.
    /// </summary>
    /// <remarks>
    /// An <see cref="Expression"/> rather than a plain method because two very different callers ask the
    /// same question and must never answer it differently: this projector asks it in memory, over rows it
    /// has already fetched, to decide what the invoice list shows; the fiscalisation console's work queue
    /// asks it in SQL, to decide whether a document is still owed. When those two disagree the console
    /// offers a Fiscalise button on a document the list is already calling fiscalised — and a duplicate
    /// fiscal receipt cannot be withdrawn.
    ///
    /// Status alone is not the rule. The manual fiscalise path writes "Success" and the desktop sync
    /// writes "Fiscalised", and a row carrying a receipt number, a QR code or a verification code is
    /// evidence whatever its status says.
    ///
    /// <c>ToLower</c> rather than an ordinal-ignore-case comparison because SQL string equality is
    /// case-sensitive on PostgreSQL and this has to mean the same thing on both sides of the wire.
    /// </remarks>
    public static readonly Expression<Func<DesktopFiscalTransactionEntity, bool>> HasFiscalEvidenceExpression =
        transaction =>
            transaction.Status.ToLower() == "success"
            || transaction.Status.ToLower() == "fiscalised"
            || transaction.ReceiptGlobalNo != null
            || (transaction.QRCode != null && transaction.QRCode.Trim() != "")
            || (transaction.VerificationCode != null && transaction.VerificationCode.Trim() != "");

    /// <summary>The negation of <see cref="HasFiscalEvidenceExpression"/>, for filtering a query down to
    /// the documents that are still owed a receipt.</summary>
    public static readonly Expression<Func<DesktopFiscalTransactionEntity, bool>> LacksFiscalEvidenceExpression =
        Expression.Lambda<Func<DesktopFiscalTransactionEntity, bool>>(
            Expression.Not(HasFiscalEvidenceExpression.Body),
            HasFiscalEvidenceExpression.Parameters);

    private static readonly Func<DesktopFiscalTransactionEntity, bool> HasFiscalEvidencePredicate =
        HasFiscalEvidenceExpression.Compile();

    public static async Task EnrichInvoicesAsync(
        ApplicationDbContext dbContext,
        IEnumerable<InvoiceDto>? invoices,
        CancellationToken cancellationToken)
    {
        if (invoices is null)
        {
            return;
        }

        var invoiceList = invoices as IReadOnlyList<InvoiceDto> ?? invoices.ToList();

        await EnrichAsync(
            dbContext,
            InvoiceDocumentType,
            invoiceList,
            invoice => invoice.DocNum,
            ApplyInvoiceStatus,
            cancellationToken);

        await ApplyConsolidatedInvoiceStatusAsync(dbContext, invoiceList, cancellationToken);
        await ApplyPerSaleInvoiceStatusAsync(dbContext, invoiceList, cancellationToken);
    }

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
            .ToDictionary(group => group.Key, group => SelectPreferredTransaction(group));

        foreach (var document in documentList)
        {
            transactionLookup.TryGetValue(docNumSelector(document), out var transaction);
            applyStatus(document, transaction);
        }
    }

    /// <summary>
    /// Reports an end-of-day consolidated invoice as fiscalised, whatever the fiscal transaction log
    /// holds for its DocNum.
    /// </summary>
    /// <remarks>
    /// It is the honest answer — every sale inside it went to FDMS before SAP, under its own receipt
    /// — and it is what keeps the invoice out of the paths that would fiscalise it a second time:
    /// <see cref="InvoiceFiscalTransactionSync.QueueUnknownInvoicesForBackfill"/> only takes
    /// invoices reading "Unknown", and the Fiscalise button is hidden on anything already fiscalised.
    ///
    /// Consolidation also writes a log row saying the same thing, with the constituent receipts in
    /// its message. This covers the invoices consolidated before that row existed, and the ones
    /// whose row failed to write — the SAP post is already committed by then, so that write can
    /// never be allowed to fail the consolidation.
    /// </remarks>
    private static async Task ApplyConsolidatedInvoiceStatusAsync(
        ApplicationDbContext dbContext,
        IReadOnlyList<InvoiceDto> invoices,
        CancellationToken cancellationToken)
    {
        if (invoices.Count == 0)
        {
            return;
        }

        var consolidatedDocNums = await ConsolidatedInvoiceRegistry.FindConsolidatedDocNumsAsync(
            dbContext,
            invoices.Select(invoice => invoice.DocNum),
            cancellationToken);

        if (consolidatedDocNums.Count == 0)
        {
            return;
        }

        foreach (var invoice in invoices.Where(invoice => consolidatedDocNums.Contains(invoice.DocNum)))
        {
            // Only the verdict. The QR code, receipt number and timestamp stay as the log left them,
            // because none of them belong to this document — they belong to its constituent receipts.
            invoice.IsFiscalized = true;
            invoice.FiscalizationStatus = FiscalisedStatus;
        }
    }

    /// <summary>
    /// Marks an invoice that records a single already-fiscalised sale as fiscalised.
    /// </summary>
    /// <remarks>
    /// The same job as <see cref="ApplyConsolidatedInvoiceStatusAsync"/>, for the routes that post one
    /// invoice per sale — van sales, shop tills and vending. Without it the invoice reads "Unknown",
    /// the backfill writes it down as "Not Fiscalised" (its lookup is by SAP DocNum, and the receipt
    /// was signed under the sale's own reference, so it finds nothing), and the Fiscalise button
    /// appears on a sale the customer is already holding a receipt for.
    ///
    /// Runs after the consolidated pass, and only widens: a document already marked fiscalised there
    /// is untouched.
    /// </remarks>
    private static async Task ApplyPerSaleInvoiceStatusAsync(
        ApplicationDbContext dbContext,
        IReadOnlyList<InvoiceDto> invoices,
        CancellationToken cancellationToken)
    {
        var unresolved = invoices
            .Where(invoice => invoice.IsFiscalized != true)
            .ToList();

        if (unresolved.Count == 0)
        {
            return;
        }

        var perSaleDocNums = await PerSaleInvoiceRegistry.FindPerSaleDocNumsAsync(
            dbContext,
            unresolved.Select(invoice => invoice.DocNum),
            cancellationToken);

        if (perSaleDocNums.Count == 0)
        {
            return;
        }

        foreach (var invoice in unresolved.Where(invoice => perSaleDocNums.Contains(invoice.DocNum)))
        {
            // Only the verdict, as above. The receipt's own number and QR belong to the sale, and are
            // read from there rather than restated on the SAP document.
            invoice.IsFiscalized = true;
            invoice.FiscalizationStatus = FiscalisedStatus;
        }
    }

    private static void ApplyInvoiceStatus(InvoiceDto invoice, DesktopFiscalTransactionEntity? transaction)
    {
        var (isFiscalized, status) = ResolveStatus(transaction);
        invoice.IsFiscalized = isFiscalized;
        invoice.FiscalizationStatus = status;
        invoice.FiscalQrCode = transaction?.QRCode;
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

    private static DesktopFiscalTransactionEntity SelectPreferredTransaction(
        IEnumerable<DesktopFiscalTransactionEntity> transactions)
        => transactions
            .OrderByDescending(HasFiscalEvidence)
            .ThenByDescending(transaction => transaction.LastSyncedAtUtc)
            .ThenByDescending(transaction => transaction.TimestampUtc)
            .First();

    private static bool HasFiscalEvidence(DesktopFiscalTransactionEntity transaction)
        => HasFiscalEvidencePredicate(transaction);
}