using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The document SAP is actually sent when stock is written off.
/// </summary>
/// <remarks>
/// Every one of these can be got wrong without anything looking wrong at the time. A goods issue is
/// a <c>Document</c>, the same entity type as an invoice, so the payload compiles and serialises just
/// as happily with a <c>CardCode</c> or a price on it — and SAP would then value the write-off at a
/// number this system invented, or file it against a business partner. The reason field exists on one
/// document line table and not another, so sending it blind refuses the document. And a batch-managed
/// line with no selection loses the whole document, not the line.
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class StockWriteOffSapPayloadTests
{
    [Fact]
    public async Task A_write_off_posts_a_goods_issue_and_lets_SAP_decide_what_it_is_worth()
    {
        var sap = new GoodsIssueServiceLayer();
        var client = CreateClient(sap);

        var issued = await client.CreateGoodsIssueAsync(new CreateGoodsIssueRequest
        {
            WarehouseCode = "RETURNS",
            Comments = "Write-off #7 out of RETURNS — Breakage.",
            JournalMemo = "Stock write-off",
            SapReference = "write-off-7",
            Lines =
            [
                new CreateGoodsIssueLineRequest { ItemCode = "PLAIN01", Quantity = 4 }
            ]
        });

        Assert.Equal(9001, issued.DocEntry);
        Assert.Equal(4471, issued.DocNum);

        // The entity set, which is the whole difference between a write-off and a transfer.
        Assert.Equal("InventoryGenExits", sap.PostedResource);

        var document = sap.PostedDocument();

        // Not a business-partner document. A CardCode here would file the write-off against a
        // customer and, on some company databases, price it from their price list.
        Assert.False(document.TryGetProperty("CardCode", out _));

        Assert.Equal("write-off-7", document.GetProperty("Reference2").GetString());
        Assert.Equal("Stock write-off", document.GetProperty("JournalMemo").GetString());

        var line = document.GetProperty("DocumentLines")[0];
        Assert.Equal("PLAIN01", line.GetProperty("ItemCode").GetString());
        Assert.Equal(4, line.GetProperty("Quantity").GetDecimal());

        // One warehouse per line, named on the line: a goods issue has one side.
        Assert.Equal("RETURNS", line.GetProperty("WarehouseCode").GetString());

        // The two omissions that decide what the write-off is worth and where it lands. Without a
        // price SAP values the line at the item's cost; without an account code SAP's own item and
        // warehouse determination charges it, exactly as it would for one keyed into B1 by hand.
        Assert.False(line.TryGetProperty("Price", out _));
        Assert.False(line.TryGetProperty("UnitPrice", out _));
        Assert.False(line.TryGetProperty("AccountCode", out _));
    }

    [Fact]
    public async Task The_reason_goes_to_SAP_only_where_the_goods_issue_line_table_defines_the_field()
    {
        var sap = new GoodsIssueServiceLayer { DefinesReasonField = true };
        var client = CreateClient(sap);

        await client.CreateGoodsIssueAsync(BatchLineRequest("Expired"));

        // Found by name on IGE1, not RIN1: the same user field is a different definition on each
        // line table, and a company database may carry one, both or neither.
        Assert.Contains("TableName eq 'IGE1'", sap.LastFieldQuery);
        Assert.Contains("Name eq 'Reasons'", sap.LastFieldQuery);

        Assert.Equal("Expired", sap.PostedDocument().GetProperty("DocumentLines")[0]
            .GetProperty("U_Reasons").GetString());
    }

    [Fact]
    public async Task A_reason_the_goods_issue_line_table_cannot_hold_is_left_off_rather_than_refused()
    {
        var sap = new GoodsIssueServiceLayer { DefinesReasonField = false };
        var client = CreateClient(sap);

        await client.CreateGoodsIssueAsync(BatchLineRequest("Expired"));

        // Naming a user field the company database does not define makes SAP refuse the document, so
        // the write-off goes without one and the reason survives on the local record instead.
        Assert.False(sap.PostedDocument().GetProperty("DocumentLines")[0]
            .TryGetProperty("U_Reasons", out _));
    }

    [Fact]
    public async Task The_batch_selection_is_sent_as_SAP_expects_to_be_told_it()
    {
        var sap = new GoodsIssueServiceLayer();
        var client = CreateClient(sap);

        await client.CreateGoodsIssueAsync(BatchLineRequest(reason: null));

        var batches = sap.PostedDocument().GetProperty("DocumentLines")[0].GetProperty("BatchNumbers");
        Assert.Equal(1, batches.GetArrayLength());
        Assert.Equal("B-0099", batches[0].GetProperty("BatchNumber").GetString());
        Assert.Equal(3, batches[0].GetProperty("Quantity").GetDecimal());
    }

    [Fact]
    public async Task A_batch_managed_line_with_no_batch_is_refused_before_anything_is_posted()
    {
        var sap = new GoodsIssueServiceLayer();
        var client = CreateClient(sap);

        var request = new CreateGoodsIssueRequest
        {
            WarehouseCode = "RETURNS",
            Lines = [new CreateGoodsIssueLineRequest { ItemCode = "BATCHED01", Quantity = 3 }]
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateGoodsIssueAsync(request));

        Assert.Contains("batch-managed", exception.Message);

        // Refused here rather than by SAP, because SAP refuses the whole document for one such line:
        // nineteen good lines would be lost with it.
        Assert.Null(sap.PostedResource);
    }

    [Fact]
    public async Task A_refusal_from_SAP_says_the_document_was_never_created()
    {
        var sap = new GoodsIssueServiceLayer { RefuseWith = "There is not enough stock in warehouse RETURNS" };
        var client = CreateClient(sap);

        var exception = await Assert.ThrowsAsync<SapRequestRejectedException>(
            () => client.CreateGoodsIssueAsync(BatchLineRequest(reason: null)));

        // The type is the point: SAP answered, and the answer was no, so the stock definitively did
        // not leave and a caller may post again. A bare exception would be indistinguishable from a
        // failure to read the reply of a document SAP had already committed.
        Assert.Contains("not enough stock", exception.SapMessage);
        Assert.Equal("create the goods issue", exception.Operation);
    }

    private static CreateGoodsIssueRequest BatchLineRequest(string? reason) => new()
    {
        WarehouseCode = "RETURNS",
        SapReference = "write-off-12",
        Lines =
        [
            new CreateGoodsIssueLineRequest
            {
                ItemCode = "BATCHED01",
                Quantity = 3,
                Reason = reason,
                BatchNumbers = [new TransferBatchRequest { BatchNumber = "B-0099", Quantity = 3 }]
            }
        ]
    };

    /// <summary>
    /// A Service Layer that remembers the goods issue posted to it, can define the reason field on the
    /// goods-issue line table or not, and knows which of its items SAP manages by batch.
    /// </summary>
    private sealed class GoodsIssueServiceLayer : HttpMessageHandler
    {
        private string? _postedBody;

        public bool DefinesReasonField { get; init; }
        public string? RefuseWith { get; init; }
        public string? PostedResource { get; private set; }
        public string LastFieldQuery { get; private set; } = string.Empty;

        public JsonElement PostedDocument()
        {
            Assert.NotNull(_postedBody);
            return JsonDocument.Parse(_postedBody!).RootElement.Clone();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (target.Contains("UserFieldsMD?", StringComparison.Ordinal))
            {
                LastFieldQuery = target;
                return Json(DefinesReasonField
                    ? "{\"value\":[{\"FieldID\":6,\"Name\":\"Reasons\",\"TableName\":\"IGE1\"}]}"
                    : "{\"value\":[]}");
            }

            if (target.Contains("UserFieldsMD(", StringComparison.Ordinal))
            {
                return Json(
                    "{\"Name\":\"Reasons\",\"ValidValuesMD\":[" +
                    "{\"Value\":\"Expired\",\"Description\":\"Past its expiry date\"}," +
                    "{\"Value\":\"Breakage\",\"Description\":\"Broken in the market\"}]}");
            }

            if (target.Contains("Items?", StringComparison.Ordinal))
            {
                // BATCHED01 is managed by batch; PLAIN01 is not. Anything else is unknown to the
                // item master, which the client treats as "not read" rather than "not managed".
                var rows = new List<string>();
                if (target.Contains("BATCHED01", StringComparison.Ordinal))
                {
                    rows.Add("{\"ItemCode\":\"BATCHED01\",\"ManageBatchNumbers\":\"tYES\",\"ManageSerialNumbers\":\"tNO\"}");
                }

                if (target.Contains("PLAIN01", StringComparison.Ordinal))
                {
                    rows.Add("{\"ItemCode\":\"PLAIN01\",\"ManageBatchNumbers\":\"tNO\",\"ManageSerialNumbers\":\"tNO\"}");
                }

                return Json($"{{\"value\":[{string.Join(",", rows)}]}}");
            }

            if (target.EndsWith("/InventoryGenExits", StringComparison.Ordinal)
                && request.Method == HttpMethod.Post)
            {
                PostedResource = "InventoryGenExits";
                _postedBody = await request.Content!.ReadAsStringAsync(cancellationToken);

                if (RefuseWith is not null)
                {
                    return Json(
                        $"{{\"error\":{{\"code\":-1029,\"message\":{{\"lang\":\"en-us\",\"value\":\"{RefuseWith}\"}}}}}}",
                        HttpStatusCode.BadRequest);
                }

                return Json("{\"DocEntry\":9001,\"DocNum\":4471,\"DocumentLines\":[]}", HttpStatusCode.Created);
            }

            throw new InvalidOperationException($"Unexpected SAP request: {request.Method} {target}");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
            new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "ShopInventory.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static SAPServiceLayerClient CreateClient(GoodsIssueServiceLayer sap)
    {
        var httpClient = new HttpClient(sap) { BaseAddress = new Uri("https://sap.invalid/b1s/v1/") };
        var services = new ServiceCollection().BuildServiceProvider();

        return new SAPServiceLayerClient(
            httpClient,
            new SingleClientFactory(httpClient),
            Options.Create(new SAPSettings { ServiceLayerUrl = "https://sap.invalid/b1s/v1/" }),
            new StubHostEnvironment(),
            NullLogger<SAPServiceLayerClient>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            new CacheSyncStateRecorder(
                services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<CacheSyncStateRecorder>.Instance),
            StubProxy.Unused<ISapItemUomMappingStore>());
    }
}
