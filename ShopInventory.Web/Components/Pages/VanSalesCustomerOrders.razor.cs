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
    /// The four statuses an operator can filter to, plus the resting "Open only".
    /// </summary>
    /// <remarks>
    /// Built from the two helpers below rather than spelled out, so a status added to the model
    /// cannot arrive in the filter with a label the list already uses or a tone the badge does not.
    /// "Open only" is the empty value every filter in this app uses for its resting state, which is
    /// why it needs no IsUnset flag — NocturneSelect reads an empty string as unset already.
    /// </remarks>
    private static readonly NocturneSelectOption<string>[] StatusFilterOptions =
    [
        new(string.Empty, "Open only", "neutral") { RuleAfter = true },
        Option(VanSalesOrderStatusModel.Fulfilled),
        Option(VanSalesOrderStatusModel.PartiallyFulfilled),
        Option(VanSalesOrderStatusModel.Cancelled),
        Option(VanSalesOrderStatusModel.Expired)
    ];

    private static NocturneSelectOption<string> Option(VanSalesOrderStatusModel status) =>
        new(status.ToString(), StatusLabel(status), StatusFamily(status));

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

    /// <summary>
    /// The same status as a Nocturne family, feeding both the filter's swatch and the badge on
    /// each order. One helper, because two switches over the same statuses drift apart the first
    /// time one is added.
    /// </summary>
    /// <remarks>
    /// "Awaiting delivery" is accent rather than warn: it is the ordinary open state of an order,
    /// not something wrong. Cancelled is neutral for the mirror of that reason — a shop calling
    /// off an order is a normal outcome, and colouring it red would put it next to the one status
    /// that does need chasing, which is an order that expired undelivered.
    /// </remarks>
    private static string StatusFamily(VanSalesOrderStatusModel status) => status switch
    {
        VanSalesOrderStatusModel.Accepted => "accent",
        VanSalesOrderStatusModel.Fulfilled => "good",
        VanSalesOrderStatusModel.PartiallyFulfilled => "warn",
        VanSalesOrderStatusModel.Cancelled => "neutral",
        VanSalesOrderStatusModel.Expired => "bad",
        _ => "neutral"
    };
}
