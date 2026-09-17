using ErrorOr;
using MediatR;

namespace ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesCreditNotes;

/// <summary>
/// The credit notes raised against invoices the van sales app created.
/// </summary>
/// <remarks>
/// <para><b>Against, not by.</b> The handset raises no credit notes: a van sale is reversed at the office.
/// So "a credit note from the van sales app" can only mean one that credits a van invoice, and that is what
/// is listed — found by the invoice it names, never by a source field on the credit itself.</para>
///
/// <para>Two places record them. <b>SAP credit memos</b>, read from the local projection, whose lines name a
/// van invoice as their base document. And <b>till credits against offline van sales</b>
/// (<c>DesktopCreditNotes</c>), which are fiscalised before SAP sees them and so may not be in the projection
/// yet; one that has reached SAP is shown once, as the memo it became.</para>
/// </remarks>
public sealed record GetVanSalesCreditNotesQuery(
    DateTime FromDate,
    DateTime ToDate,
    string? State = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 50
) : IRequest<ErrorOr<VanSalesCreditNotesResult>>;

/// <remarks>
/// <para><c>SapProjectionCurrent</c>: Whether the SAP credit memo projection is being kept up to date. When it
/// is not, a memo raised in SAP recently may be missing, and the page says so.</para>
/// </remarks>
public sealed record VanSalesCreditNotesResult(
    DateTime FromDate,
    DateTime ToDate,
    int Page,
    int PageSize,
    int TotalCount,
    bool SapProjectionCurrent,
    VanSalesCreditNoteCounts Counts,
    List<VanSalesCreditNoteRow> Rows);

public sealed record VanSalesCreditNoteCounts(
    int All,
    int Complete,
    int AwaitingSap,
    int NotFiscalised,
    int InProgress,
    int NeedsAttention);

/// <remarks>
/// <para><c>Origin</c>: <c>SAP</c> for a credit memo, <c>Till</c> for a credit raised against an offline van
/// sale that SAP has not taken yet.</para>
/// <para><c>Number</c>: The number the customer's copy carries: SAP's DocNum, or the till credit's own.</para>
/// <para><c>State</c>: One of <see cref="VanSalesDocumentStates"/>.</para>
/// </remarks>
public sealed record VanSalesCreditNoteRow(
    string Key,
    string Origin,
    DateTime Date,
    string Number,
    int? SapDocEntry,
    int? SapDocNum,
    string? CustomerCode,
    string? CustomerName,
    decimal Amount,
    decimal? VatAmount,
    string Currency,
    string? Reason,
    bool IsCancelled,
    List<VanSalesCreditedInvoice> CreditedInvoices,
    string? FiscalReceiptNumber,
    string State,
    string? Problem);

/// <summary>The van invoice a credit note reverses.</summary>
public sealed record VanSalesCreditedInvoice(
    string Reference,
    int? SapDocNum,
    string? CustomerName,
    string? RepName);
