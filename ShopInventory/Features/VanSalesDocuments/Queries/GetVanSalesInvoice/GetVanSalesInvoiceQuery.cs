using ErrorOr;
using MediatR;
using ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoices;

namespace ShopInventory.Features.VanSalesDocuments.Queries.GetVanSalesInvoice;

/// <summary>One van invoice, by van order: its lines, its receipt, and how far it got.</summary>
public sealed record GetVanSalesInvoiceQuery(string Reference) : IRequest<ErrorOr<VanSalesInvoiceDetail>>;

public sealed record VanSalesInvoiceDetail(
    VanSalesInvoiceRow Invoice,
    string? FiscalQrCode,
    int PostingAttempts,
    string? QueueStatus,
    List<VanSalesInvoiceLine> Lines,
    List<VanSalesInvoiceCredit> CreditNotes);

/// <summary>A credit note against the invoice, as the invoice's drawer states it.</summary>
/// <remarks>
/// <para><c>Key</c>: the same key the credit notes list gives the note, so the page can open it there.</para>
/// <para><c>Origin</c>: <c>SAP</c> for a credit memo, <c>Till</c> for a till credit SAP has not taken yet. One
/// that SAP has taken is listed once, as the memo.</para>
/// <para><c>GivesBack</c>: whether <c>Amount</c> has actually been given back — a memo that was not cancelled,
/// or a till credit the device signed. The rest are still listed, so a refused or pending credit is seen, but
/// they are not taken off the invoice.</para>
/// </remarks>
public sealed record VanSalesInvoiceCredit(
    string Key,
    string Origin,
    string Number,
    DateTime Date,
    decimal Amount,
    string Currency,
    string? Reason,
    bool IsCancelled,
    bool GivesBack);

/// <remarks>
/// <para><c>UnitPrice</c>: As the sale recorded it: net for an online sale, as signed for an offline
/// one.</para>
/// </remarks>
public sealed record VanSalesInvoiceLine(
    int LineNum,
    string ItemCode,
    string? ItemDescription,
    decimal Quantity,
    string? UoMCode,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal LineTotal,
    string? TaxCode);
