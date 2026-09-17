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
    List<VanSalesInvoiceLine> Lines);

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
