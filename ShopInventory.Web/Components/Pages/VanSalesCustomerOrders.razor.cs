using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components.Pages;

/// <summary>
/// The back office's view of orders van sales customers placed themselves.
/// </summary>
/// <remarks>
/// Behaviour lives here rather than in the markup, per the repo's Blazor rules: the page renders,
/// this decides. The three things it decides are what to load, what "record delivery" sends, and
/// what to say when the API refuses.
/// </remarks>
public partial class VanSalesCustomerOrders
{
    private VanSalesRouteLoadModel load = new();
    private bool isBusy;

    /// <summary>
    /// Defaults to tomorrow, which is the day being loaded for.
    /// </summary>
    /// <remarks>
    /// The depot looks at this screen the afternoon before a run, so today's orders are already on
    /// a van. Opening on today would show a list nobody can act on.
    /// </remarks>
    private DateTime? visitDate = DateTime.Today.AddDays(1);

    private string? businessPartnerCode;
    private string? status;

    protected override Task OnInitializedAsync() => ReloadAsync();

    private async Task ReloadAsync()
    {
        isBusy = true;

        try
        {
            var parsedStatus = Enum.TryParse<VanSalesOrderStatusModel>(status, out var value)
                ? value
                : (VanSalesOrderStatusModel?)null;

            load = await VanSalesOrderService.GetRouteLoadAsync(
                businessPartnerCode,
                routeCode: null,
                visitDate,
                parsedStatus);

            // Seeded with the ordered quantity, because delivering everything is the common case.
            // A form starting at zero invites a hurried submit that records the whole round as
            // undelivered.
            foreach (var line in load.Orders.SelectMany(o => o.Lines))
            {
                line.DeliveredInput = line.QuantityOrdered;
            }
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task RecordDeliveryAsync(VanSalesOrderModel order)
    {
        isBusy = true;

        try
        {
            var request = new RecordVanSalesDeliveryModel
            {
                Lines = order.Lines
                    .Select(l => new RecordVanSalesDeliveryLineModel
                    {
                        LineNumber = l.LineNumber,
                        QuantityFulfilled = l.DeliveredInput
                    })
                    .ToList()
            };

            var updated = await VanSalesOrderService.RecordDeliveryAsync(order.Id, request);

            Snackbar.Add(
                $"Recorded delivery for {updated.OrderNumber}: {StatusLabel(updated.Status).ToLowerInvariant()}.",
                MudBlazor.Severity.Success);

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            // The API's own sentence — "more was delivered than ordered for FRM001" — rather than a
            // generic failure. It names what to change.
            Snackbar.Add(ex.Message, MudBlazor.Severity.Error);
        }
        finally
        {
            isBusy = false;
        }
    }

    private async Task ConvertAsync(VanSalesOrderModel order)
    {
        isBusy = true;

        try
        {
            var result = await VanSalesOrderService.ConvertAsync(order.Id);

            Snackbar.Add(
                $"{result.VanSalesOrderNumber} converted to sales order {result.SalesOrderNumber}.",
                MudBlazor.Severity.Success);

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Snackbar.Add(ex.Message, MudBlazor.Severity.Error);
        }
        finally
        {
            isBusy = false;
        }
    }

    /// <summary>
    /// The status in an operator's words.
    /// </summary>
    /// <remarks>
    /// "Part delivered" rather than "PartiallyFulfilled": this is the row someone will be asked
    /// about by a shop, and the label should read the way the conversation will.
    /// </remarks>
    private static string StatusLabel(VanSalesOrderStatusModel status) => status switch
    {
        VanSalesOrderStatusModel.Accepted => "Awaiting delivery",
        VanSalesOrderStatusModel.Fulfilled => "Delivered",
        VanSalesOrderStatusModel.PartiallyFulfilled => "Part delivered",
        VanSalesOrderStatusModel.Cancelled => "Cancelled",
        VanSalesOrderStatusModel.Expired => "Not delivered",
        _ => status.ToString()
    };
}
