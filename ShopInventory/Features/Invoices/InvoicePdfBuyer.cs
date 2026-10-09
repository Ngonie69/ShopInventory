namespace ShopInventory.Features.Invoices;

/// <summary>
/// Who an invoice is printed as being for, when that is not the SAP card it was posted to.
/// </summary>
/// <remarks>
/// A van posts every sale to its own selling account, so SAP's CardName on the invoice is the van. The
/// shop that bought is a route customer kept here, not a business partner, and it is the shop that
/// receives the invoice. Given a buyer, the PDF names it in the invoice address and the reference, and
/// no business partner is read: the van's VAT number, TIN and phone are not the shop's.
/// </remarks>
/// <param name="Name">The shop's name.</param>
/// <param name="Address">Its address, as kept on the route customer.</param>
/// <param name="VatNumber">Its VAT number, if it gave one.</param>
/// <param name="Phone">Its phone.</param>
/// <param name="Email">Its email.</param>
public sealed record InvoicePdfBuyer(
    string Name,
    string? Address,
    string? VatNumber,
    string? Phone,
    string? Email);
