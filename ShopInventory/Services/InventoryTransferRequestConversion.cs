using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// The inventory transfer an inventory transfer request becomes.
/// </summary>
/// <remarks>
/// One definition, used both by <see cref="SAPServiceLayerClient.ConvertTransferRequestToTransferAsync"/>
/// to post and by <c>ConvertTransferRequestHandler</c> to check the depot's stock beforehand, so the
/// document that was checked is the document that posts.
/// </remarks>
public static class InventoryTransferRequestConversion
{
    /// <summary>
    /// How far before the request's own date a transfer made from it may be dated. SAP dates a
    /// document by its business day, which can trail the server clock, so the lookup reaches back.
    /// </summary>
    private static readonly TimeSpan LookupSlack = TimeSpan.FromDays(2);

    /// <summary>
    /// What a transfer made from this request carries in SAP's <c>U_OrderNumber</c>.
    /// </summary>
    /// <remarks>
    /// SAP keeps no link of its own here: the transfer is posted fresh rather than copied from the
    /// request, so it has no <c>BaseEntry</c>. This number is how a later conversion finds an earlier
    /// one, whether its reply was lost or the request failed to close after it posted.
    /// </remarks>
    public static string OrderNumberFor(int requestDocEntry) => $"ITR-{requestDocEntry}";

    /// <summary>
    /// The earliest date a transfer made from this request can carry. Every conversion is dated on
    /// or after the request, so the lookup never needs to read further back than this.
    /// </summary>
    public static DateTime LookupFrom(InventoryTransferRequest transferRequest, DateTime today)
    {
        var raised = DateTime.TryParse(
            transferRequest.DocDate,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AllowWhiteSpaces,
            out var parsed)
            ? parsed.Date
            // A request with no readable date is searched for a year, which is long enough
            // for any request still open to be converted from.
            : today.Date.AddYears(-1);

        return raised - LookupSlack;
    }

    public static CreateInventoryTransferRequest ToTransfer(InventoryTransferRequest transferRequest)
    {
        // Allocation is deliberately not done here: CreateInventoryTransferAsync resolves the
        // management flags and allocates every line of the document it is about to post, so a second
        // implementation here could only disagree with it — and did, by allocating each line from the
        // full batch pool regardless of what the other lines had already claimed.
        var lines = transferRequest.StockTransferLines?
            .Select(requestLine => new CreateInventoryTransferLineRequest
            {
                ItemCode = requestLine.ItemCode,
                Quantity = requestLine.Quantity,
                FromWarehouseCode = requestLine.FromWarehouseCode ?? transferRequest.FromWarehouse ?? "01",
                ToWarehouseCode = requestLine.WarehouseCode ?? transferRequest.ToWarehouse
            })
            .ToList() ?? [];

        return new CreateInventoryTransferRequest
        {
            FromWarehouse = transferRequest.FromWarehouse,
            ToWarehouse = transferRequest.ToWarehouse,
            DocDate = DateTime.Today.ToString("yyyy-MM-dd"),
            DueDate = transferRequest.DueDate,
            Comments = $"Converted from Transfer Request #{transferRequest.DocNum}. {transferRequest.Comments}".Trim(),
            OrderNumber = OrderNumberFor(transferRequest.DocEntry),
            Lines = lines
        };
    }
}
