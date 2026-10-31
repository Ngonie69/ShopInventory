using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopInventory.Features.Revmax.Commands.TransactMExt;
using ShopInventory.Features.Revmax.Queries.GetRevmaxInvoice;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Controllers;

/// <summary>
/// Direct calls to the REVMax fiscal device, for an admin working a receipt by hand.
/// </summary>
/// <remarks>
/// REVMax has filed nothing new since the platform took over on 2026-09-30. These two routes are for
/// looking up what it filed and for crediting a receipt another device filed.
/// </remarks>
[Route("api/revmax")]
[Authorize(Policy = "AdminOnly")]
public class RevmaxController(IMediator mediator) : ApiControllerBase
{
    /// <summary>REVMax's <c>GetInvoice/{invoiceNumber}</c>: the receipt the device holds for a number.</summary>
    /// <remarks>
    /// Read-only. The device answers with whichever device's receipt carries the number, so check
    /// <c>DeviceID</c>. 404 means REVMax said it holds no such invoice; 400 means it gave no clear answer.
    /// </remarks>
    [HttpGet("invoices/{invoiceNumber}")]
    [ProducesResponseType(typeof(InvoiceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvoice([FromRoute] string invoiceNumber, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(new GetRevmaxInvoiceQuery(invoiceNumber), cancellationToken);
        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }

    /// <summary>
    /// REVMax's <c>TransactMExt</c>: files a credit note against a receipt another device filed.
    /// </summary>
    /// <remarks>
    /// <b>This files a receipt with ZIMRA and cannot be undone.</b> It is sent once and never retried.
    ///
    /// Refused before sending when REVMax already holds <c>InvoiceNumber</c> (409), when it cannot say
    /// whether it does, or when <c>refDeviceId</c> is REVMax's own device (that credit is filed through
    /// TransactM by the desktop credit-note flow). A 400 saying the outcome is unknown means check
    /// <c>GET /api/revmax/invoices/{invoiceNumber}</c> before sending again.
    ///
    /// Every field travels as a string. <c>ItemsXml</c> is a list of
    /// <c>{ "HH", "ITEMCODE", "ITEMNAME1", "ITEMNAME2", "QTY", "PRICE", "AMT", "TAX", "TAXR" }</c> (neither
    /// name may be blank) and <c>CurrenciesXml</c> a list of <c>{ "Name", "Amount", "Rate" }</c>.
    ///
    ///     {
    ///       "Currency": "USD", "BranchName": "Kefalos", "InvoiceNumber": "CN-12345",
    ///       "OriginalInvoiceNumber": "12345", "CustomerName": "Customer", "InvoiceAmount": "10",
    ///       "InvoiceTaxAmount": "1.34", "Istatus": "02", "Cashier": "admin", "InvoiceComment": "Returned goods",
    ///       "refDeviceId": "12345", "refReceiptGlobalNo": "678", "refFiscalDayNo": "90",
    ///       "ItemsXml": [ { "HH": "1", "ITEMCODE": "ITEM1", "ITEMNAME1": "Item", "ITEMNAME2": "Item",
    ///                      "QTY": "1", "PRICE": "10", "AMT": "10", "TAX": "1", "TAXR": "15.5" } ],
    ///       "CurrenciesXml": [ { "Name": "USD", "Amount": "10", "Rate": "1" } ]
    ///     }
    ///
    /// A 200 means REVMax filed it (<c>Code</c> "1") and carries the device's answer. The device itself
    /// refuses with HTTP 200 and <c>Code</c> "0"; that comes back here as a 400 with its message.
    /// </remarks>
    [HttpPost("transact-m-ext")]
    [ProducesResponseType(typeof(TransactMExtResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> TransactMExt(
        [FromBody] TransactMExtRequest request,
        CancellationToken cancellationToken)
    {
        // Filing must not stop half-way because the caller hung up. See
        // committed-post-must-not-take-request-token in the project notes.
        var result = await mediator.Send(
            new RevmaxTransactMExtCommand(request, User.Identity?.Name),
            CancellationToken.None);
        return result.Match<IActionResult>(Ok, errors => Problem(errors));
    }
}
