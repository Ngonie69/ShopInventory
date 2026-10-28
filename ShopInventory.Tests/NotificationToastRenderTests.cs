using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Web.Components.Layout;
using ShopInventory.Web.Models;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The staff toast headlines a document number only when it has one. A notification's EntityId is
/// the producer's de-duplication key, and only a sales order's happens to be readable: a transfer
/// approval's is "DocEntry:stage-guid", which the toast once printed as its headline.
/// </summary>
public sealed class NotificationToastRenderTests
{
    [Fact]
    public async Task A_transfer_approval_shows_its_title_and_message_not_its_entity_key()
    {
        var html = await RenderAsync(new NotificationModel
        {
            Title = "Transfer #82423 needs approval",
            Message = "Depot manager: HRE-MAIN to BYO-CENTRE. Requested by tendai.",
            Type = "Alert",
            Category = "TransferApproval",
            EntityType = "TransferRequestApproval",
            EntityId = "82423:110000000000000000000000000003",
            ActionUrl = "/inventory-transfers?requestDocEntry=82423",
            CreatedAt = DateTime.UtcNow,
            Data = new Dictionary<string, string>
            {
                ["requestDocEntry"] = "82423",
                ["requestDocNum"] = "82423",
                ["stageName"] = "Depot manager"
            }
        });

        Assert.DoesNotContain("110000000000000000000000000003", html);
        Assert.Contains("Transfer #82423 needs approval", html);
        Assert.Contains("HRE-MAIN to BYO-CENTRE. Requested by tendai.", html);
        Assert.Contains("Transfer Approval", html);
        Assert.Contains("Review", html);
    }

    [Fact]
    public async Task A_factory_transfer_does_not_headline_its_guid()
    {
        var id = Guid.NewGuid().ToString();
        var html = await RenderAsync(new NotificationModel
        {
            Title = "FT-0042 is on its way to BYO-CENTRE",
            Message = "Rudo sent 120 unit(s) from FACTORY. Count it when it arrives, then accept it.",
            Type = "Info",
            Category = "TransferApproval",
            EntityType = "FactoryTransfer",
            EntityId = id,
            ActionUrl = "/factory-transfers",
            CreatedAt = DateTime.UtcNow
        });

        Assert.DoesNotContain(id, html);
        Assert.Contains("FT-0042 is on its way to BYO-CENTRE", html);
        Assert.Contains(">Open<", html.Replace(" ", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty));
    }

    [Fact]
    public async Task A_sales_order_still_headlines_its_order_number_from_the_entity_id()
    {
        var html = await RenderAsync(new NotificationModel
        {
            Title = "New sales order",
            Message = "Order SO-1001 for Spar Avondale",
            Type = "Info",
            Category = "SalesOrder",
            EntityType = "SalesOrder",
            EntityId = "SO-1001",
            ActionUrl = "/sales-orders",
            CreatedAt = DateTime.UtcNow
        });

        Assert.Contains("kf-toast--detail", html);
        Assert.Contains("SO-1001", html);
        Assert.Contains("View order", html);
    }

    private static async Task<string> RenderAsync(NotificationModel notification)
    {
        var services = new ServiceCollection();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<NotificationToast>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(NotificationToast.Notification)] = notification
                }));

            return output.ToHtmlString();
        });
    }
}
