using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Hubs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Cancelling a receipt: the reason SAP is given, and the till that is told.
/// </summary>
/// <remarks>
/// Three things here can fail without anything looking wrong at the time, which is why they are
/// pinned rather than eyeballed. The reason can be dropped on the way to SAP and the credit note
/// still posts. The reason field can be located by an id that is right in one company database and
/// wrong in another. And a warehouse group can be joined in one case and sent to in another, which
/// SignalR answers by delivering to nobody and reporting success.
/// </remarks>
[Collection("SapServiceLayerClient")]
public sealed class InvoiceCancellationTests
{
    [Fact]
    public async Task Credit_note_lines_carry_the_reason_into_SAPs_own_field()
    {
        var sap = new ReasonAwareServiceLayer();
        var client = CreateClient(sap);

        await client.CreateCreditNoteAsync(new CreateCreditNoteRequest
        {
            CardCode = "C-1",
            Reason = "Invoice cancelled: Customer order not collected",
            OriginalInvoiceDocEntry = 4242,
            Lines =
            [
                new() { ItemCode = "ITEM-A", Quantity = 3, UnitPrice = 1.5m, ReturnReason = "Cancellation", OriginalInvoiceLineId = 0 },
                new() { ItemCode = "ITEM-B", Quantity = 1, UnitPrice = 2.5m, ReturnReason = "Cancellation", OriginalInvoiceLineId = 1 }
            ]
        });

        // Every line, not just the first: U_Reasons is a line field, so a cancellation that set it
        // on one line would report in SAP as a partial return of the others.
        Assert.Equal(["Cancellation", "Cancellation"], sap.PostedLineReasons("CreditNotes"));
    }

    [Fact]
    public async Task Standalone_credit_note_carries_the_reason_too()
    {
        var sap = new ReasonAwareServiceLayer();
        var client = CreateClient(sap);

        await client.CreateCreditNoteAsync(new CreateCreditNoteRequest
        {
            CardCode = "C-1",
            Reason = "Crates back",
            Lines = [new() { ItemCode = "ITEM-A", Quantity = 1, UnitPrice = 1m, ReturnReason = "Crates" }]
        });

        Assert.Equal(["Crates"], sap.PostedLineReasons("CreditNotes"));
    }

    [Fact]
    public async Task A_line_with_no_reason_leaves_the_field_out_so_SAP_applies_its_default()
    {
        var sap = new ReasonAwareServiceLayer();
        var client = CreateClient(sap);

        await client.CreateCreditNoteAsync(new CreateCreditNoteRequest
        {
            CardCode = "C-1",
            Reason = "No reason on the line",
            Lines = [new() { ItemCode = "ITEM-A", Quantity = 1, UnitPrice = 1m }]
        });

        // Absent rather than empty. The field carries a default value in SAP, and an empty string
        // is a value — it would overwrite that default with nothing.
        Assert.False(sap.LineHasProperty("CreditNotes", lineIndex: 0, "U_Reasons"));
    }

    [Fact]
    public async Task Reasons_are_found_by_field_name_not_by_a_field_id()
    {
        // The same user field is FieldID 5 in the production company database and 6 in the test one.
        // A client that remembered an id would read a different field, or none, against the other.
        var sap = new ReasonAwareServiceLayer { ReasonFieldId = 6 };
        var client = CreateClient(sap);

        var reasons = await client.GetCreditNoteLineReasonsAsync();

        Assert.Equal(
            [("Cancellation", "Customer order not collected"), ("Crates", "Crates returned by customer")],
            reasons.Select(reason => (reason.Value, reason.Description)));
        Assert.Contains("Name eq 'Reasons'", sap.LastFieldQuery);
        Assert.Contains("FieldID=6", sap.LastFieldRead);
    }

    [Fact]
    public async Task Reading_the_reason_list_asks_for_a_page_big_enough_to_hold_it()
    {
        // The Service Layer answers 20 rows without a maxpagesize preference and this company
        // database defines over 700 user fields, so an unpaged read finds no Reasons row at all and
        // reports the field as missing.
        var sap = new ReasonAwareServiceLayer();
        var client = CreateClient(sap);

        await client.GetCreditNoteLineReasonsAsync();

        Assert.Contains("odata.maxpagesize", sap.LastPreferHeader);
    }

    [Fact]
    public async Task A_company_database_with_no_reason_field_answers_an_empty_list()
    {
        var sap = new ReasonAwareServiceLayer { DefinesReasonField = false };
        var client = CreateClient(sap);

        Assert.Empty(await client.GetCreditNoteLineReasonsAsync());
    }

    [Fact]
    public async Task A_till_joins_the_group_for_every_warehouse_on_its_account()
    {
        var groups = new RecordingGroupManager();
        var hub = ConnectedHub(groups, warehouses: ["farm", "CORTINA"]);

        await hub.OnConnectedAsync();

        // Normalised, because SignalR group names are compared byte for byte while warehouse codes
        // are not: a till that joined "farm" and a send addressed to "FARM" is a message delivered
        // to nobody, reported as a success.
        Assert.Contains("warehouse:FARM", groups.Joined);
        Assert.Contains("warehouse:CORTINA", groups.Joined);
        Assert.Equal(NotificationHub.WarehouseGroup("farm"), NotificationHub.WarehouseGroup("FARM "));
    }

    [Fact]
    public async Task An_account_with_no_warehouse_joins_no_warehouse_group()
    {
        var groups = new RecordingGroupManager();
        var hub = ConnectedHub(groups, warehouses: []);

        await hub.OnConnectedAsync();

        Assert.DoesNotContain(groups.Joined, group => group.StartsWith("warehouse:", StringComparison.Ordinal));
        Assert.Contains("all", groups.Joined);
    }

    private static NotificationHub ConnectedHub(RecordingGroupManager groups, string[] warehouses)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "till-1"), new(ClaimTypes.Role, "Cashier") };
        claims.AddRange(warehouses.Select(warehouse => new Claim("warehouse", warehouse)));

        return new NotificationHub(NullLogger<NotificationHub>.Instance)
        {
            Context = new StubHubCallerContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))),
            Groups = groups
        };
    }

    private sealed class RecordingGroupManager : IGroupManager
    {
        public List<string> Joined { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubHubCallerContext(ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => "connection-1";
        public override string? UserIdentifier => user.Identity?.Name;
        public override ClaimsPrincipal? User => user;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    /// <summary>A Service Layer that remembers what was posted and can define the reason field or not.</summary>
    private sealed class ReasonAwareServiceLayer : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _posted = new(StringComparer.Ordinal);

        public bool DefinesReasonField { get; init; } = true;
        public int ReasonFieldId { get; init; } = 5;
        public string LastFieldQuery { get; private set; } = string.Empty;
        public string LastFieldRead { get; private set; } = string.Empty;
        public string LastPreferHeader { get; private set; } = string.Empty;

        public List<string> PostedLineReasons(string resource)
        {
            Assert.True(_posted.ContainsKey(resource), $"No {resource} document was posted");
            using var document = JsonDocument.Parse(_posted[resource]);
            return document.RootElement.GetProperty("DocumentLines")
                .EnumerateArray()
                .Select(line => line.GetProperty("U_Reasons").GetString()!)
                .ToList();
        }

        public bool LineHasProperty(string resource, int lineIndex, string propertyName)
        {
            using var document = JsonDocument.Parse(_posted[resource]);
            return document.RootElement.GetProperty("DocumentLines")[lineIndex]
                .TryGetProperty(propertyName, out _);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var target = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);

            if (request.Headers.TryGetValues("Prefer", out var prefer))
            {
                LastPreferHeader = string.Join(";", prefer);
            }

            if (target.EndsWith("/Login", StringComparison.Ordinal))
            {
                return Json("{\"SessionId\":\"test-session\"}");
            }

            if (target.Contains("UserFieldsMD?", StringComparison.Ordinal))
            {
                LastFieldQuery = target;
                return Json(DefinesReasonField
                    ? $"{{\"value\":[{{\"FieldID\":{ReasonFieldId},\"Name\":\"Reasons\",\"TableName\":\"RIN1\"}}]}}"
                    : "{\"value\":[]}");
            }

            if (target.Contains("UserFieldsMD(", StringComparison.Ordinal))
            {
                LastFieldRead = target;
                return Json(
                    "{\"Name\":\"Reasons\",\"ValidValuesMD\":[" +
                    "{\"Value\":\"Cancellation\",\"Description\":\"Customer order not collected\"}," +
                    "{\"Value\":\"Crates\",\"Description\":\"Crates returned by customer\"}]}");
            }

            if (target.EndsWith("/CreditNotes", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                _posted["CreditNotes"] = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json("{\"DocEntry\":501,\"DocNum\":601,\"DocumentLines\":[]}", HttpStatusCode.Created);
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

    private static SAPServiceLayerClient CreateClient(ReasonAwareServiceLayer sap)
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
